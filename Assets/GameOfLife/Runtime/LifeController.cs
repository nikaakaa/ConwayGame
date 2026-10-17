using System;
using System.Collections.Generic;
using System.IO;
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
        public LifePattern SelectedPattern { get; private set; } = LifePatterns.Items[0];
        public LifeState State => manager.GetComponentData<LifeState>(boardEntity);
        public float Speed => manager.GetComponentData<LifeSettings>(boardEntity).GenerationsPerSecond;
        public long Population => simulation.Simulation.Population;
        public LifePatternStore Store { get; private set; }
        public string PatternsDirectory { get; private set; }
        public string PatternName { get; set; } = "新结构";

        /// <summary>选区为闭区间世界坐标；只有框选工具下才存在。</summary>
        public bool HasSelection { get; private set; }
        public int2 SelectionMin => math.min(selectionAnchor, selectionEnd);
        public int2 SelectionMax => math.max(selectionAnchor, selectionEnd);

        /// <summary>需要提示用户的结果，例如复制成功或剪贴板无法识别。</summary>
        public event Action<string> Notified;
        public event Action PatternsChanged;

        EntityManager manager;
        Entity boardEntity;
        LifeSimulationSystem simulation;
        PointerEventData.InputButton activeButton;
        bool hasPreviousCell;
        int2 previousCell;
        int2 panStartOrigin;
        Vector2 panStartPosition;
        float nextSpeedRepeatTime;
        int2 selectionAnchor;
        int2 selectionEnd;
        bool selectionMoved;
        readonly List<int2> scratch = new List<int2>();

        void Awake()
        {
            PatternsDirectory = Path.Combine(Application.persistentDataPath, "Patterns");
            Store = new LifePatternStore(PatternsDirectory);
        }

        void Start()
        {
            manager = World.DefaultGameObjectInjectionWorld.EntityManager;
            simulation = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LifeSimulationSystem>();
            EntityQuery query = manager.CreateEntityQuery(ComponentType.ReadOnly<LifeState>());
            boardEntity = query.GetSingletonEntity();
            query.Dispose();
            simulation.LoadPatterns(Store.Patterns);
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
            if (keyboard.escapeKey.wasPressedThisFrame)
                ClearSelection();
            if (keyboard.digit0Key.wasPressedThisFrame)
                UseCellBrush();
            for (int i = 0; i < LifePatterns.Items.Length; i++)
                if (keyboard[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame)
                    SelectPattern(LifePatterns.Items[i]);

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

        void SetBrush(LifeBrushMode brush)
        {
            if (brush != LifeBrushMode.Select)
                ClearSelection();
            Brush = brush;
            hasPreviousCell = false;
            View.SetGhost(brush != LifeBrushMode.Select, brush == LifeBrushMode.Pattern
                ? simulation.PatternShape(SelectedPattern) : simulation.CellShape);
        }

        public void ClearSelection()
        {
            HasSelection = false;
            View.SetSelection(false, default, default);
        }

        /// <summary>有选区时取框内活细胞，否则取整个棋盘。</summary>
        List<int2> CollectTargetCells()
        {
            simulation.Simulation.CollectCells(scratch, HasSelection, SelectionMin, SelectionMax);
            return scratch;
        }

        public void SaveCells()
        {
            List<int2> cells = CollectTargetCells();
            if (cells.Count == 0)
            {
                Notify(HasSelection ? "选区内没有活细胞" : "棋盘是空的");
                return;
            }
            LifePattern pattern = Store.Save(PatternName, cells);
            simulation.LoadPatterns(Store.Patterns);
            PatternsChanged?.Invoke();
            SelectPattern(pattern);
            Notify("已保存 " + pattern.Name);
        }

        public void DeleteSelection()
        {
            if (!HasSelection)
                return;
            Submit(new LifeCommand { Kind = LifeCommandKind.ClearArea, Position = SelectionMin, End = SelectionMax });
        }

        public void CopyToClipboard()
        {
            List<int2> cells = CollectTargetCells();
            if (cells.Count == 0)
            {
                Notify(HasSelection ? "选区内没有活细胞" : "棋盘是空的");
                return;
            }
            GUIUtility.systemCopyBuffer = LifePatternText.ToRle(PatternName, cells);
            Notify("已复制 " + cells.Count + " 格");
        }

        public void PasteFromClipboard()
        {
            if (!LifePatternText.TryParse(GUIUtility.systemCopyBuffer, "导入", out LifePattern parsed, out string error))
            {
                Notify(error);
                return;
            }
            LifePattern pattern = Store.Import(parsed);
            simulation.LoadPatterns(Store.Patterns);
            PatternsChanged?.Invoke();
            SelectPattern(pattern);
            Notify("已导入 " + pattern.Name);
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
            simulation.LoadPatterns(Store.Patterns);
            PatternsChanged?.Invoke();
            SelectPattern(Store.Patterns[0]);
        }

        void Notify(string message) => Notified?.Invoke(message);

        public void ToggleRunning()
        {
            Submit(new LifeCommand { Kind = State.IsRunning ? LifeCommandKind.Pause : LifeCommandKind.Play });
        }

        public void Step() => Submit(new LifeCommand { Kind = LifeCommandKind.Step });

        public void Clear() => Submit(new LifeCommand { Kind = LifeCommandKind.Clear });

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
                panStartPosition = eventData.position;
            }
            else if (activeButton == PointerEventData.InputButton.Left && Brush == LifeBrushMode.Select)
            {
                if (View.TryGetCell(eventData.position, eventData.pressEventCamera, out int2 cell))
                {
                    selectionAnchor = selectionEnd = cell;
                    selectionMoved = false;
                    HasSelection = true;
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
                int2 cells = (int2)math.round(new float2(delta.x / rect.rect.width, delta.y / rect.rect.height) * View.VisibleCells);
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
            bool inside = View.TryGetCell(position, eventCamera, out int2 cell);
            View.SetHover(inside, cell);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hasPreviousCell = false;
            View.SetHover(false, default);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (eventData.scrollDelta.y == 0)
                return;
            View.Zoom(eventData.position, eventData.enterEventCamera, eventData.scrollDelta.y > 0);
            hasPreviousCell = false;
            UpdateHover(eventData.position, eventData.enterEventCamera);
        }
    }
}
