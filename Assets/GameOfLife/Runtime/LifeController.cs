using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace GameOfLife
{
    public enum LifeBrushMode
    {
        Cell,
        Pattern,
        Select
    }

    public sealed class LifeController : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IDragHandler, IInitializePotentialDragHandler, IPointerExitHandler, IPointerMoveHandler, IScrollHandler
    {
        public LifeView View;

        public const float MinSpeed = 1f;
        public const float MaxSpeed = 1073741824f;
        public bool KeyboardShortcutsEnabled { get; set; } = true;

        public LifeBrushMode Brush { get; private set; } = LifeBrushMode.Cell;
        public LifePattern SelectedPattern { get; private set; }
        public LifeState State => manager.GetComponentData<LifeState>(boardEntity);
        public float Speed => manager.GetComponentData<LifeSettings>(boardEntity).GenerationsPerSecond;
        public LifePopulationSample PopulationSample => simulation.Simulation.PopulationSample;
        public LifeSession Session { get; private set; }
        public LifePatternStore Store => Session.Patterns;

        /// <summary>选区为闭区间世界坐标；只有框选工具下才存在。</summary>
        public bool HasSelection { get; private set; }
        /// <summary>正在拖出选区；松手前不显示选区操作。</summary>
        public bool IsSelecting { get; private set; }
        public int2 SelectionMin => math.min(selectionAnchor, selectionEnd);
        public int2 SelectionMax => math.max(selectionAnchor, selectionEnd);

        /// <summary>需要提示用户的结果，例如复制成功或剪贴板无法识别。</summary>
        public event Action<string> Notified;

        EntityManager manager;
        Entity boardEntity;
        LifeSimulationSystem simulation;
        PointerEventData.InputButton activeButton;
        bool hasPreviousCell;
        int2 previousCell;
        int2 strokeWindowOrigin;
        int strokeWindowCells;
        int2 panStartOrigin;
        int panStartCells;
        Vector2 panStartPosition;
        float nextSpeedRepeatTime;
        int2 selectionAnchor;
        int2 selectionEnd;
        bool selectionMoved;
        // 盖章之前使用的画笔或框选，Esc 取消盖章时回到它。
        LifeBrushMode previousTool = LifeBrushMode.Cell;

        void Start()
        {
            simulation = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LifeSimulationSystem>();
            manager = simulation.EntityManager;
            boardEntity = simulation.Board;
            Session = simulation.Session;
            if (!Session.HasWorld)
                Session.NewWorld();
            SelectedPattern = Store.Patterns[0];
            Session.PatternStored += OnPatternStored;
            View.Bind(manager, boardEntity, simulation.Simulation);
            SetBrush(Brush);
        }

        void Update()
        {
            if (!KeyboardShortcutsEnabled)
                return;
            Keyboard keyboard = Keyboard.current;
            if (keyboard.ctrlKey.isPressed)
            {
                if (keyboard.cKey.wasPressedThisFrame)
                    CopyToClipboard();
                if (keyboard.vKey.wasPressedThisFrame)
                    PasteFromClipboard();
                return;
            }

            if (keyboard.sKey.wasPressedThisFrame)
                UseSelectTool();
            if (keyboard.deleteKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame)
                DeleteSelection();
            if (keyboard.digit0Key.wasPressedThisFrame)
                UseCellBrush();

            if (keyboard.spaceKey.wasPressedThisFrame)
                ToggleRunning();
            if (keyboard.nKey.wasPressedThisFrame)
                Step();
            if (keyboard.cKey.wasPressedThisFrame)
                Clear();
            if (keyboard.rKey.wasPressedThisFrame)
                ResetPattern();
            if (keyboard.homeKey.wasPressedThisFrame)
                View.ResetWindow();

            int speedDirection = (keyboard.upArrowKey.isPressed ? 1 : 0)
                - (keyboard.downArrowKey.isPressed ? 1 : 0);
            if (speedDirection != 0 && (keyboard.upArrowKey.wasPressedThisFrame
                || keyboard.downArrowKey.wasPressedThisFrame || Time.unscaledTime >= nextSpeedRepeatTime))
            {
                ChangeSpeed(speedDirection > 0 ? 2f : 0.5f);
                nextSpeedRepeatTime = Time.unscaledTime + 0.12f;
            }
        }

        public void UseCellBrush() => SetBrush(LifeBrushMode.Cell);

        public void SelectPattern(LifePattern pattern)
        {
            SelectedPattern = pattern;
            SetBrush(LifeBrushMode.Pattern);
        }

        public void UseSelectTool() => SetBrush(LifeBrushMode.Select);

        /// <summary>Esc：先取消选区；没有选区时退出盖章（包括 Ctrl+V 导入后的盖章），回到之前的工具。</summary>
        public void Cancel()
        {
            if (HasSelection)
                ClearSelection();
            else if (Brush == LifeBrushMode.Pattern)
                SetBrush(previousTool);
        }

        void SetBrush(LifeBrushMode brush)
        {
            if (brush != LifeBrushMode.Select)
                ClearSelection();
            if (brush != LifeBrushMode.Pattern)
                previousTool = brush;
            Brush = brush;
            hasPreviousCell = false;
            View.SetGhost(brush != LifeBrushMode.Select, brush == LifeBrushMode.Pattern
                ? simulation.PatternShape(SelectedPattern) : simulation.CellShape);
        }

        public void ClearSelection()
        {
            HasSelection = false;
            IsSelecting = false;
            View.SetSelection(false, default, default);
        }

        public async Task<LifePattern> StoreCellsAsync(string name)
        {
            bool selected = HasSelection;
            Notify(selected ? "正在收集选区" : "正在收集世界快照");
            LifePattern pattern = await Session.StoreCellsAsync(name, selected, SelectionMin, SelectionMax);
            if (pattern == null)
                Notify(selected ? "选区内没有活细胞" : "棋盘是空的");
            return pattern;
        }

        void OnPatternStored(LifePattern pattern)
        {
            SetBrush(Brush);
            Notify("已存入 " + pattern.Population + " 格");
        }

        void OnDestroy()
        {
            Session.PatternStored -= OnPatternStored;
        }

        public void DeleteSelection()
        {
            if (!HasSelection)
                return;
            Submit(new LifeCommand { Kind = LifeCommandKind.ClearArea, Position = SelectionMin, End = SelectionMax });
        }

        public void CopyToClipboard()
        {
            StartCoroutine(CopySnapshot());
        }

        IEnumerator CopySnapshot()
        {
            bool selected = HasSelection;
            Notify(selected ? "正在收集选区" : "正在收集世界快照");
            Task<int2[]> capture = simulation.Simulation.CollectCellsAsync(selected, SelectionMin, SelectionMax);
            while (!capture.IsCompleted)
                yield return null;
            int2[] cells = capture.GetAwaiter().GetResult();
            if (cells.Length == 0)
            {
                Notify(selected ? "选区内没有活细胞" : "棋盘是空的");
                yield break;
            }
            GUIUtility.systemCopyBuffer = LifePatternText.ToRle(string.Empty, cells);
            Notify("已复制 " + cells.Length + " 格");
        }

        /// <summary>从剪贴板导入结构并设为盖章笔刷；无法识别时返回 null。</summary>
        public LifePattern PasteFromClipboard()
        {
            if (!LifePatternText.TryParse(GUIUtility.systemCopyBuffer, "导入", out LifePattern parsed, out string error))
            {
                Notify(error);
                return null;
            }
            LifePattern pattern = Store.Import(parsed);
            PatternsChanged();
            SelectPattern(pattern);
            Notify("已导入 " + pattern.Name);
            return pattern;
        }

        /// <summary>
        /// 结构列表变化后重建盖章形状。模拟只认已加载的结构，当前结构若已不在库中，
        /// 按名称找回（重新读取或改名后对象会替换），找不到时改用第一个。
        /// </summary>
        void PatternsChanged(LifePattern replacement = null)
        {
            simulation.LoadPatterns(Store.Patterns);
            if (replacement != null)
                SelectedPattern = replacement;
            else if (!Contains(SelectedPattern))
            {
                LifePattern match = Store.Patterns[0];
                foreach (LifePattern pattern in Store.Patterns)
                    if (pattern.Name == SelectedPattern.Name)
                    {
                        match = pattern;
                        break;
                    }
                SelectedPattern = match;
            }
            SetBrush(Brush);
        }

        bool Contains(LifePattern pattern)
        {
            foreach (LifePattern candidate in Store.Patterns)
                if (candidate == pattern)
                    return true;
            return false;
        }

        /// <summary>目录在外部发生变化时才重新读取，打开结构库时调用。</summary>
        public void ReloadPatternsIfChanged()
        {
            try
            {
                if (Store.ReloadIfChanged())
                    PatternsChanged();
            }
            catch (FormatException exception)
            {
                Notify(exception.Message);
            }
        }

        public void ReloadPatterns()
        {
            try
            {
                Store.Reload();
            }
            catch (FormatException exception)
            {
                Notify(exception.Message);
                return;
            }
            PatternsChanged();
        }

        /// <summary>结构库中复制单个结构的 RLE。</summary>
        public void CopyPattern(LifePattern pattern)
        {
            GUIUtility.systemCopyBuffer = LifePatternText.ToRle(pattern);
            Notify("已复制 " + pattern.Name);
        }

        public void DeletePattern(LifePattern pattern)
        {
            if (!Store.CanDelete(pattern))
            {
                Notify("库中至少保留一个结构");
                return;
            }
            Store.Delete(pattern);
            PatternsChanged();
            Notify("已删除 " + pattern.Name);
        }

        /// <summary>改名并返回替换后的结构；若正被选中，选中项随之替换。</summary>
        public LifePattern RenamePattern(LifePattern pattern, string name)
        {
            LifePattern renamed = Store.Rename(pattern, name);
            if (renamed != pattern)
                PatternsChanged(SelectedPattern == pattern ? renamed : null);
            return renamed;
        }

        void Notify(string message) => Notified?.Invoke(message);

        public void ToggleRunning()
        {
            Submit(new LifeCommand { Kind = State.IsRunning ? LifeCommandKind.Pause : LifeCommandKind.Play });
        }

        public void Step() => Submit(new LifeCommand { Kind = LifeCommandKind.Step });

        public void Clear() => Submit(new LifeCommand { Kind = LifeCommandKind.Clear });

        /// <summary>
        /// 用 <see cref="LifeWorldStore.LoadAsync"/> 解析出的完整世界替换当前世界。结构临时加入盖章形状集合，
        /// 等待中的盖章指令会被回收器视为根，应用前不会被回收。视野以载入位置为中心，代数从零开始。
        /// </summary>
        /// <param name="save">来自存档时为该存档的列表项，之后保存会覆盖它；地图为 null。</param>
        public void LoadWorld(LifePattern world, int2 anchor, LifePattern save)
        {
            Session.LoadWorld(world, anchor, save);
            ClearSelection();
            View.RestoreViewport();
            hasPreviousCell = false;
            // 形状集合已重建，刷新当前笔刷的预览形状。
            SetBrush(Brush);
        }

        public void ResetPattern()
        {
            DynamicBuffer<LifeCommand> commands = manager.GetBuffer<LifeCommand>(boardEntity);
            commands.EnsureCapacity(commands.Length + 2);
            commands.Add(new LifeCommand { Kind = LifeCommandKind.Clear });
            commands.Add(new LifeCommand { Kind = LifeCommandKind.PatternStroke,
                Shape = simulation.PatternShape(SelectedPattern) });
            View.ResetWindow();
            hasPreviousCell = false;
        }

        public void SetSpeed(float speed)
        {
            Submit(new LifeCommand { Kind = LifeCommandKind.SetSpeed, Speed = math.clamp(speed, MinSpeed, MaxSpeed) });
        }

        public void ChangeSpeed(float factor) => SetSpeed(Speed * factor);

        void Submit(LifeCommand command)
        {
            manager.GetBuffer<LifeCommand>(boardEntity).Add(command);
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            eventData.useDragThreshold = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            activeButton = eventData.button;
            hasPreviousCell = false;
            if (activeButton == PointerEventData.InputButton.Middle)
            {
                panStartOrigin = View.Origin;
                panStartCells = View.VisibleCells;
                panStartPosition = eventData.position;
            }
            else if (activeButton == PointerEventData.InputButton.Left && Brush == LifeBrushMode.Select)
            {
                if (View.TryGetCell(eventData.position, eventData.pressEventCamera, out int2 cell))
                {
                    selectionAnchor = selectionEnd = cell;
                    selectionMoved = false;
                    HasSelection = true;
                    IsSelecting = true;
                    View.SetSelection(true, SelectionMin, SelectionMax);
                }
            }
            else
                Paint(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != activeButton)
                return;
            hasPreviousCell = false;
            IsSelecting = false;
            // 单击不拖动视为取消选区。
            if (activeButton == PointerEventData.InputButton.Left && Brush == LifeBrushMode.Select && !selectionMoved)
                ClearSelection();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != activeButton)
                return;
            if (activeButton == PointerEventData.InputButton.Middle)
            {
                RectTransform rect = View.BoardImage.rectTransform;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, panStartPosition, eventData.pressEventCamera, out Vector2 start);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, eventData.pressEventCamera, out Vector2 current);
                Vector2 delta = current - start;
                int2 cells = (int2)math.round(new float2(delta.x / rect.rect.width, delta.y / rect.rect.height) * panStartCells);
                View.Pan(panStartOrigin - cells - View.Origin);
                UpdateHover(eventData.position, eventData.pressEventCamera);
            }
            else if (activeButton == PointerEventData.InputButton.Left && Brush == LifeBrushMode.Select)
            {
                // 拖出棋盘时仍按指针位置延伸选区。
                if (!HasSelection)
                    return;
                View.TryGetCell(eventData.position, eventData.pressEventCamera, out int2 cell);
                if (math.any(cell != selectionEnd))
                {
                    selectionEnd = cell;
                    selectionMoved = true;
                    View.SetSelection(true, SelectionMin, SelectionMax);
                }
            }
            else
            {
                UpdateHover(eventData.position, eventData.pressEventCamera);
                if (eventData.pointerCurrentRaycast.gameObject != gameObject)
                {
                    hasPreviousCell = false;
                    return;
                }
                Paint(eventData);
            }
        }

        void Paint(PointerEventData eventData)
        {
            if (!View.TryGetCell(eventData.position, eventData.pressEventCamera, out int2 cell))
            {
                hasPreviousCell = false;
                return;
            }
            if (math.any(strokeWindowOrigin != View.Origin) || strokeWindowCells != View.VisibleCells)
                hasPreviousCell = false;
            strokeWindowOrigin = View.Origin;
            strokeWindowCells = View.VisibleCells;
            if (!hasPreviousCell || math.any(cell != previousCell))
            {
                bool stamp = activeButton != PointerEventData.InputButton.Right && Brush == LifeBrushMode.Pattern;
                Submit(new LifeCommand
                {
                    Kind = stamp ? LifeCommandKind.PatternStroke : LifeCommandKind.CellStroke,
                    Position = hasPreviousCell ? previousCell : cell,
                    End = cell,
                    SkipFirst = hasPreviousCell,
                    Alive = activeButton != PointerEventData.InputButton.Right,
                    Shape = stamp ? simulation.PatternShape(SelectedPattern) : default
                });
            }
            previousCell = cell;
            hasPreviousCell = true;
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            UpdateHover(eventData.position, eventData.enterEventCamera);
        }

        void UpdateHover(Vector2 position, Camera eventCamera)
        {
            View.SetHover(position, eventCamera);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hasPreviousCell = false;
            View.ClearHover();
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (eventData.scrollDelta.y == 0)
                return;
            View.Zoom(eventData.position, eventData.enterEventCamera, eventData.scrollDelta.y > 0);
            if (activeButton == PointerEventData.InputButton.Middle)
            {
                panStartOrigin = View.Origin;
                panStartCells = View.VisibleCells;
                panStartPosition = eventData.position;
            }
            hasPreviousCell = false;
            UpdateHover(eventData.position, eventData.enterEventCamera);
        }
    }
}
