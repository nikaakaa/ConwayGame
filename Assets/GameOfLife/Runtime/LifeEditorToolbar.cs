using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace GameOfLife
{
    /// <summary>
    /// 逐层展开的极简界面：收起时只有滑翔机；展开后为演化、编辑、世界三个方块图标，
    /// 每个都点击展开二级（再点或 Esc 收起，移开鼠标不收起）。选区松手后在右下角浮出存入、复制、删除。
    /// 图标形状统一取自 LifeIcons。
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class LifeEditorToolbar : MonoBehaviour
    {
        public LifeController Controller;
        public LifeView View;

        const string OnClass = "life-dock__item--on";
        const string ExpandedKey = "GameOfLife.DockExpanded";
        const float ToastSeconds = 2f;

        sealed class DockItem
        {
            public VisualElement Element;
            public LifeCellIcon Icon;
            public string Tip;
            public Action Click;
            public Action<VisualElement> BuildSub;
        }

        VisualElement root;
        VisualElement dock;
        VisualElement level1;
        VisualElement sub;
        Label tip;
        Label hint;
        bool hintVisible;
        VisualElement status;
        LifeNumber generationValue;
        LifeNumber populationValue;
        Label toast;
        VisualElement selectionBar;
        VisualElement help;
        LifePatternLibrary library;
        LifePatternLibrary savesLibrary;
        LifePatternLibrary mapsLibrary;
        LifeStartScreen startScreen;
        // 视觉树重建时恢复开始界面；首次构建时显示。
        bool startVisible = true;
        bool startCanResume;
        bool saving;

        DockItem glider;
        DockItem playItem;
        DockItem editItem;
        DockItem worldItem;
        readonly List<DockItem> subItems = new List<DockItem>();
        DockItem openParent;
        DockItem playToggle;
        Label speedLabel;
        DockItem editPencil;
        DockItem editSelect;
        DockItem editStamp;

        Font font;
        Font monoFont;
        bool expanded;
        bool hoveringGlider;
        float toastHideTime;
        long shownGeneration = -1;
        long shownPopulation = -1;
        LifeSnapshot shownPopulationSnapshot;
        int shownPhase = -1;
        float shownSpeed = -1f;
        LifePattern shownPattern;
        LifeBrushMode shownBrush = (LifeBrushMode)(-1);
        bool shownRunning;
        readonly Vector3[] boardCorners = new Vector3[4];
        PanelSettings panelSettings;
        int scaledHeight;

        void Start()
        {
            font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
            monoFont = Font.CreateDynamicFontFromOSFont("Consolas", 14);
            expanded = PlayerPrefs.GetInt(ExpandedKey, 1) != 0;
            Controller.Notified += ShowToast;
            // 运行时副本，缩放修改不写回资源。
            UIDocument document = GetComponent<UIDocument>();
            panelSettings = Instantiate(document.panelSettings);
            document.panelSettings = panelSettings;
            UpdateScale();
            Build();
        }

        /// <summary>以 1080 像素高为 1 倍，窗口更高时等比放大；小窗口保持 1 倍，图标不缩小到难以点击。</summary>
        void UpdateScale()
        {
            if (Screen.height == scaledHeight)
                return;
            scaledHeight = Screen.height;
            panelSettings.scale = math.max(1f, Screen.height / 1080f);
        }

        /// <summary>UIDocument 重建视觉树（例如编辑器内样式热重载）后，界面需要重新创建。</summary>
        void Build()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.style.unityFontDefinition = FontDefinition.FromFont(font);
            library?.Dispose();
            savesLibrary?.Dispose();
            mapsLibrary?.Dispose();
            startScreen?.Dispose();
            subItems.Clear();
            openParent = null;
            shownGeneration = -1;
            shownPopulation = -1;
            shownPopulationSnapshot = default;
            shownPhase = -1;
            shownSpeed = -1f;
            shownPattern = null;
            shownRunning = false;
            shownBrush = (LifeBrushMode)(-1);

            dock = Add(root, new VisualElement(), "life-dock");
            glider = CreateItem(dock, LifeIcons.GliderPhases[0], null, ToggleExpanded);
            glider.Element.AddToClassList("life-dock__glider");
            glider.Element.RegisterCallback<PointerEnterEvent>(_ => { hoveringGlider = true; UpdateStatusVisibility(); });
            glider.Element.RegisterCallback<PointerLeaveEvent>(_ => { hoveringGlider = false; UpdateStatusVisibility(); });

            level1 = Add(dock, new VisualElement(), "life-dock__level");
            // 一级只有三组，全部点击展开二级；编辑图标显示当前工具或结构。
            playItem = CreateItem(level1, LifeIcons.Time, "演化", null, BuildPlaySub);
            editItem = CreateItem(level1, LifeIcons.Pencil, "编辑", null, BuildEditSub);
            worldItem = CreateItem(level1, LifeIcons.Menu, "世界", null, BuildWorldSub);

            // 状态与操作反馈都在图标行末尾：悬停滑翔机看代数和人口，操作后短暂显示结果。
            status = Add(dock, new VisualElement(), "life-dock__status");
            status.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            Add(status, new Label("GEN"), "life-dock__status-key");
            generationValue = Add(status, new LifeNumber(), "life-dock__status-value");
            Add(status, new Label("POP~"), "life-dock__status-key");
            populationValue = Add(status, new LifeNumber(), "life-dock__status-value");
            toast = Add(dock, new Label(), "life-dock__toast");
            hint = Add(dock, new Label(), "life-dock__hint");
            hint.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            hint.pickingMode = PickingMode.Ignore;

            sub = Add(root, new VisualElement(), "life-dock__sub");
            sub.style.display = DisplayStyle.None;

            BuildSelectionBar();
            BuildHelp();

            library = new LifePatternLibrary(root, monoFont, () => Controller.Store.Patterns, Controller.Store.CanDelete)
            {
                Title = "结构",
                StoreRequested = Controller.StoreCellsAsync,
                ImportRequested = Controller.PasteFromClipboard,
                RenameRequested = Controller.RenamePattern,
                DeleteRequested = Controller.DeletePattern,
                CopyRequested = Controller.CopyPattern,
                FolderRequested = () => Application.OpenURL(new Uri(Controller.PatternsDirectory).AbsoluteUri)
            };
            library.Picked += Controller.SelectPattern;
            library.OpenChanged += _ => UpdateShortcuts();

            // 存档与地图复用结构库界面，选中即替换整个世界；地图只读。列表项不是完整结构：
            // 缩略图是保存时或离线生成的图片，不演化；存档和地图都只能载入，不复制。
            savesLibrary = new LifePatternLibrary(root, monoFont, () => Controller.Worlds.Saves, Controller.Worlds.IsSave)
            {
                RenameRequested = Controller.RenameSave,
                DeleteRequested = Controller.DeleteSave,
                PreviewImage = Controller.Worlds.Preview,
                AnimatePreviews = false,
                Title = "存档",
                PickTip = "载入  ↵"
            };
            savesLibrary.Picked += EnterWorld;
            savesLibrary.OpenChanged += _ => UpdateShortcuts();
            mapsLibrary = new LifePatternLibrary(root, monoFont, () => Controller.Worlds.Maps, _ => false)
            {
                PreviewImage = Controller.Worlds.Preview,
                AnimatePreviews = false,
                Title = "地图",
                PickTip = "载入  ↵"
            };
            mapsLibrary.Picked += EnterWorld;
            mapsLibrary.OpenChanged += _ => UpdateShortcuts();

            startScreen = new LifeStartScreen(root, monoFont);
            startScreen.NewWorldRequested += () =>
            {
                Controller.NewWorld();
                HideStart();
            };
            startScreen.ContinueRequested += ContinueWorld;
            startScreen.SavesRequested += () => savesLibrary.Open(Controller.CurrentSave);
            startScreen.MapsRequested += OpenMaps;
            startScreen.QuitRequested += QuitApplication;

            // 提示最后加入，盖在二级菜单和选区操作条之上。
            tip = Add(root, new Label(), "life-dock__tip");
            tip.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            tip.style.display = DisplayStyle.None;

            ApplyExpanded(false);
            UpdateStatusVisibility();
            if (startVisible)
                ShowStart(startCanResume);
        }

        static T Add<T>(VisualElement parent, T element, string className) where T : VisualElement
        {
            element.AddToClassList(className);
            parent.Add(element);
            return element;
        }

        // ---------- 图标与二级菜单 ----------

        DockItem CreateItem(VisualElement parent, string[] shape, string tipText, Action click,
            Action<VisualElement> buildSub = null)
        {
            var item = new DockItem { Tip = tipText, Click = click, BuildSub = buildSub };
            item.Element = Add(parent, new VisualElement(), "life-dock__item");
            item.Icon = Add(item.Element, new LifeCellIcon(), "life-dock__icon");
            item.Icon.SetShape(shape);
            if (buildSub != null)
                Add(item.Element, new VisualElement(), "life-dock__dot").pickingMode = PickingMode.Ignore;

            // 悬停只显示提示；有二级的图标点击展开或收起，其余执行操作。
            item.Element.RegisterCallback<PointerEnterEvent>(_ => ShowTip(item));
            item.Element.RegisterCallback<PointerLeaveEvent>(_ => HideTip());
            item.Element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0)
                    return;
                if (item.BuildSub != null)
                    ToggleSub(item);
                else
                    item.Click?.Invoke();
                evt.StopPropagation();
            });
            return item;
        }

        void ToggleSub(DockItem item)
        {
            if (openParent == item)
                CloseSub();
            else
                OpenSub(item);
            ShowTip(item);
        }

        void OpenSub(DockItem item)
        {
            openParent?.Element.RemoveFromClassList("life-dock__item--open");
            openParent = item;
            sub.Clear();
            subItems.Clear();
            ResetSubReferences();
            item.BuildSub(sub);
            Rect anchor = item.Element.worldBound;
            sub.style.left = anchor.x;
            sub.style.top = anchor.yMax + 4f;
            sub.style.display = DisplayStyle.Flex;
            item.Element.AddToClassList("life-dock__item--open");
            for (int i = 0; i < subItems.Count; i++)
                PlayBirthLater(subItems[i].Icon, i * 30);
            shownSpeed = -1f;
            RefreshSubStates();
        }

        void CloseSub()
        {
            openParent?.Element.RemoveFromClassList("life-dock__item--open");
            openParent = null;
            sub.style.display = DisplayStyle.None;
            sub.Clear();
            subItems.Clear();
            ResetSubReferences();
        }

        void ResetSubReferences()
        {
            playToggle = null;
            speedLabel = null;
            editPencil = null;
            editSelect = null;
            editStamp = null;
        }

        DockItem SubItem(VisualElement parent, string[] shape, string tipText, Action click)
        {
            DockItem item = CreateItem(parent, shape, tipText, click);
            subItems.Add(item);
            return item;
        }

        void BuildPlaySub(VisualElement parent)
        {
            // 原来单击一级图标的运行/暂停放在二级第一个。
            playToggle = SubItem(parent, shownRunning ? LifeIcons.Pause : LifeIcons.Play,
                shownRunning ? "暂停  Space" : "运行  Space", Controller.ToggleRunning);
            SubItem(parent, LifeIcons.Step, "单步  N", Controller.Step);
            SubItem(parent, LifeIcons.Slower, "减速  ↓", () => Controller.ChangeSpeed(0.5f));
            speedLabel = Add(parent, new Label(), "life-dock__speed");
            speedLabel.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            speedLabel.RegisterCallback<PointerEnterEvent>(_ => ShowHint("代/秒  滚轮调整"));
            speedLabel.RegisterCallback<PointerLeaveEvent>(_ => HideTip());
            speedLabel.RegisterCallback<WheelEvent>(evt =>
            {
                Controller.ChangeSpeed(evt.delta.y < 0 ? 2f : 0.5f);
                evt.StopPropagation();
            });
            SubItem(parent, LifeIcons.Faster, "加速  ↑", () => Controller.ChangeSpeed(2f));
        }

        void BuildEditSub(VisualElement parent)
        {
            editPencil = SubItem(parent, LifeIcons.Pencil, "画笔  0", Controller.UseCellBrush);
            editSelect = SubItem(parent, LifeIcons.Select, "框选  S", Controller.UseSelectTool);
            editStamp = SubItem(parent, LifeIcons.Use, string.Empty, () => Controller.SelectPattern(Controller.SelectedPattern));
            RefreshStamp();
            SubItem(parent, LifeIcons.Library, "结构库  L", OpenLibrary);
        }

        void BuildWorldSub(VisualElement parent)
        {
            SubItem(parent, LifeIcons.Clear, "清空  C", Controller.Clear);
            SubItem(parent, LifeIcons.Reset, "重置  R", Controller.ResetPattern);
            SubItem(parent, LifeIcons.Save, "保存", SaveWorld);
            SubItem(parent, LifeIcons.Home, "开始界面", () => ShowStart(true));
            SubItem(parent, LifeIcons.Help, "帮助  ?", () =>
            {
                CloseSub();
                HideTip();
                SetHelp(true);
            });
            SubItem(parent, LifeIcons.Quit, "退出程序", QuitApplication);
        }

        static void QuitApplication()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------- 开始界面 ----------

        void ShowStart(bool canResume)
        {
            CloseSub();
            HideTip();
            if (Controller.State.IsRunning)
                Controller.ToggleRunning();
            startVisible = true;
            startCanResume = canResume;
            Controller.Worlds.ReloadSaves();
            startScreen.Show(canResume || Controller.Worlds.Saves.Count > 0);
            UpdateShortcuts();
        }

        void ContinueWorld()
        {
            if (startCanResume)
                HideStart();
            else
                EnterWorld(Controller.Worlds.Saves[0]);
        }

        void HideStart()
        {
            startVisible = false;
            startScreen.Hide();
            UpdateShortcuts();
        }

        void EnterWorld(LifePattern world)
        {
            if (!startScreen.IsLoading)
                StartCoroutine(EnterWorldAfterFrame(world));
        }

        // 列表项只是名称与缩略图：选中后才在后台读取解析完整世界，期间滑翔机照常飞行；
        // 编译进模拟必须在主线程同步完成，先让载入画面绘制出来再开始。
        IEnumerator EnterWorldAfterFrame(LifePattern entry)
        {
            startScreen.ShowLoading();
            Task<(LifePattern World, int2 Anchor)> load = Controller.Worlds.LoadAsync(entry);
            while (!load.IsCompleted)
                yield return null;
            if (load.IsFaulted)
            {
                Debug.LogException(load.Exception);
                startScreen.HideLoading();
                yield break;
            }
            yield return null;
            yield return null;
            try
            {
                Controller.LoadWorld(load.Result.World, load.Result.Anchor,
                    Controller.Worlds.IsSave(entry) ? entry : null);
            }
            finally
            {
                startScreen.HideLoading();
            }
            HideStart();
        }

        void SaveWorld()
        {
            CloseSub();
            HideTip();
            if (saving)
                return;
            Task<string> save = Controller.SaveWorldAsync(out LifeController.SaveTicket ticket);
            StartCoroutine(FinishSave(save, ticket));
        }

        // 保存在后台进行，不挡住编辑；完成后刷新存档列表并提示。
        IEnumerator FinishSave(Task<string> save, LifeController.SaveTicket ticket)
        {
            saving = true;
            while (!save.IsCompleted)
                yield return null;
            saving = false;
            if (save.IsFaulted)
            {
                Debug.LogException(save.Exception);
                ShowToast("保存失败");
            }
            else if (save.Result != null)
                Controller.CompleteSave(ticket, save.Result);
        }

        // 列表只读目录，不读地图文件；点选某个地图时才读取解析并载入。
        void OpenMaps()
        {
            Controller.Worlds.ReloadMaps();
            mapsLibrary.Open(null);
        }

        /// <summary>开始界面、任一浮层或帮助打开时，棋盘快捷键停用。</summary>
        void UpdateShortcuts()
        {
            Controller.KeyboardShortcutsEnabled = !startVisible && !library.IsOpen && !savesLibrary.IsOpen
                && !mapsLibrary.IsOpen && !HelpOpen;
        }

        /// <summary>编辑图标显示当前工具；盖章时显示所选结构的缩略图。</summary>
        void RefreshEditIcon()
        {
            LifeBrushMode brush = Controller.Brush;
            LifePattern pattern = Controller.SelectedPattern;
            if (brush == shownBrush && pattern == shownPattern)
                return;
            shownBrush = brush;
            shownPattern = pattern;
            if (brush == LifeBrushMode.Pattern)
                editItem.Icon.SetPattern(pattern);
            else
                editItem.Icon.SetShape(brush == LifeBrushMode.Select ? LifeIcons.Select : LifeIcons.Pencil);
            editItem.Tip = brush == LifeBrushMode.Pattern ? pattern.Name + "  Esc 退出盖章"
                : brush == LifeBrushMode.Select ? "框选" : "画笔";
            RefreshStamp();
        }

        void RefreshStamp()
        {
            if (editStamp == null)
                return;
            editStamp.Icon.SetPattern(Controller.SelectedPattern);
            editStamp.Tip = "盖章  " + Controller.SelectedPattern.Name;
        }

        void RefreshSubStates()
        {
            if (openParent == playItem && speedLabel != null && Controller.Speed != shownSpeed)
            {
                shownSpeed = Controller.Speed;
                speedLabel.text = shownSpeed.ToString("0") + "/s";
            }
            if (openParent == editItem && editPencil != null)
            {
                LifeBrushMode brush = Controller.Brush;
                editPencil.Element.EnableInClassList(OnClass, brush == LifeBrushMode.Cell);
                editSelect.Element.EnableInClassList(OnClass, brush == LifeBrushMode.Select);
                editStamp.Element.EnableInClassList(OnClass, brush == LifeBrushMode.Pattern);
            }
        }

        // 提示从不放在图标下方，避免被鼠标光标挡住：图标行与二级的提示显示在一级图标行右侧，
        // 二级在一级下方，因此提示总在光标上方；选区操作条浮在棋盘中间，提示放在操作条上方。
        void ShowTip(DockItem item)
        {
            if (string.IsNullOrEmpty(item.Tip))
                return;
            if (selectionBar.Contains(item.Element))
            {
                Rect bar = selectionBar.worldBound;
                tip.text = item.Tip;
                tip.style.left = bar.x;
                tip.style.bottom = root.layout.height - bar.y + 4f;
                tip.style.display = DisplayStyle.Flex;
                return;
            }
            ShowHint(item.Tip);
        }

        void ShowHint(string text)
        {
            hint.text = text;
            hintVisible = true;
            UpdateStatusVisibility();
        }

        void HideTip()
        {
            tip.style.display = DisplayStyle.None;
            hintVisible = false;
            UpdateStatusVisibility();
        }

        static void PlayBirthLater(LifeCellIcon icon, long delay)
        {
            if (delay <= 0)
            {
                icon.PlayBirth();
                return;
            }
            icon.style.visibility = Visibility.Hidden;
            icon.schedule.Execute(() =>
            {
                icon.style.visibility = Visibility.Visible;
                icon.PlayBirth();
            }).StartingIn(delay);
        }

        // ---------- 工具与展开 ----------

        void ToggleExpanded()
        {
            expanded = !expanded;
            PlayerPrefs.SetInt(ExpandedKey, expanded ? 1 : 0);
            ApplyExpanded(true);
        }

        void ApplyExpanded(bool animate)
        {
            CloseSub();
            level1.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            if (expanded && animate)
            {
                PlayBirthLater(playItem.Icon, 0);
                PlayBirthLater(editItem.Icon, 40);
                PlayBirthLater(worldItem.Icon, 80);
            }
        }

        void UpdateStatusVisibility()
        {
            // 同一位置依次让位：操作结果 > 悬停提示 > 代数与人口。
            bool showToast = toastHideTime > 0f;
            toast.style.display = showToast ? DisplayStyle.Flex : DisplayStyle.None;
            hint.style.display = hintVisible && !showToast ? DisplayStyle.Flex : DisplayStyle.None;
            status.style.display = hoveringGlider && !showToast && !hintVisible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ShowToast(string message)
        {
            toast.text = message;
            toastHideTime = Time.unscaledTime + ToastSeconds;
            UpdateStatusVisibility();
        }

        // 打开时检查目录，外部放入的文件无需手动刷新；目录未变时不重新解析。
        void OpenLibrary()
        {
            CloseSub();
            Controller.ReloadPatternsIfChanged();
            library.Open(Controller.SelectedPattern);
        }

        // ---------- 选区操作条 ----------

        void BuildSelectionBar()
        {
            selectionBar = Add(root, new VisualElement(), "life-dock__selection");
            selectionBar.style.display = DisplayStyle.None;
            CreateItem(selectionBar, LifeIcons.Store, "存入结构库", StoreSelection);
            CreateItem(selectionBar, LifeIcons.Copy, "复制  Ctrl+C", Controller.CopyToClipboard);
            CreateItem(selectionBar, LifeIcons.Delete, "删除  Delete", Controller.DeleteSelection);

        }

        // 存入后打开结构库并直接进入改名，回车确认。
        void StoreSelection()
        {
            HideTip();
            OpenLibrary();
            library.StoreNew();
        }

        /// <summary>选区右下角在面板中的位置；选区不在屏幕内时返回 false。</summary>
        bool TryGetSelectionAnchor(out Vector2 position)
        {
            RectTransform board = View.BoardImage.rectTransform;
            board.GetWorldCorners(boardCorners);
            Vector2 bottomLeft = boardCorners[0];
            Vector2 size = (Vector2)boardCorners[2] - bottomLeft;
            // 闭区间选区的右边界为 max.x + 1，底边为 min.y；覆盖层画布的世界坐标即屏幕像素。
            float2 corner = new float2(Controller.SelectionMax.x + 1, Controller.SelectionMin.y) - (float2)View.Origin;
            Vector2 screen = bottomLeft + new Vector2(corner.x / View.VisibleCells * size.x,
                corner.y / View.VisibleCells * size.y);
            float scale = root.layout.width / Screen.width;
            position = new Vector2(screen.x, Screen.height - screen.y) * scale;
            return position.x >= 0f && position.y >= 0f
                && position.x <= root.layout.width && position.y <= root.layout.height;
        }

        void UpdateSelectionBar()
        {
            // 拖动中不显示，避免操作条跟着鼠标跑、挡在光标旁边；松手后才出现。
            if (!Controller.HasSelection || Controller.IsSelecting || !TryGetSelectionAnchor(out Vector2 position))
            {
                selectionBar.style.display = DisplayStyle.None;
                return;
            }
            float width = selectionBar.layout.width;
            selectionBar.style.left = math.min(position.x + 6f, root.layout.width - width - 6f);
            selectionBar.style.top = math.min(position.y + 6f, root.layout.height - selectionBar.layout.height - 6f);
            selectionBar.style.display = DisplayStyle.Flex;
        }

        // ---------- 快捷键帮助 ----------

        static readonly string[,] HelpRows =
        {
            { "Space", "运行 / 暂停", "左键", "画 · 盖章 · 框选" },
            { "N", "单步", "右键", "擦除" },
            { "↑ ↓", "加速 / 减速", "中键拖动", "平移" },
            { "0  S", "画笔 / 框选", "滚轮", "缩放" },
            { "L", "结构库", "Home", "回到原点" },
            { "C", "清空", "Ctrl+C", "复制" },
            { "R", "重置", "Ctrl+V", "导入" },
            { "Tab", "收起 / 展开", "Delete", "删除选区" },
            { "?", "帮助", "Esc", "取消选区 / 盖章" },
            { "/", "库内搜索", "F2", "库内改名" },
        };

        void BuildHelp()
        {
            help = Add(root, new VisualElement(), "life-help-backdrop");
            help.style.display = DisplayStyle.None;
            help.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == help)
                    SetHelp(false);
            });
            VisualElement panel = Add(help, new VisualElement(), "life-help");
            for (int row = 0; row < HelpRows.GetLength(0); row++)
            {
                VisualElement line = Add(panel, new VisualElement(), "life-help__row");
                for (int column = 0; column < 4; column++)
                {
                    Label label = Add(line, new Label(HelpRows[row, column]),
                        column % 2 == 0 ? "life-help__key" : "life-help__text");
                    if (column % 2 == 0)
                        label.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
                }
            }
            Label close = Add(panel, new Label("?  关闭"), "life-help__footer");
            close.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
        }

        bool HelpOpen => help.style.display == DisplayStyle.Flex;

        void SetHelp(bool visible)
        {
            help.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateShortcuts();
        }

        // ---------- 每帧 ----------

        void Update()
        {
            // 先记录本帧开始时是否有浮层，避免用 Esc 关闭存档列表的同一帧又触发开始界面的“返回”。
            bool overlayOpen = library.IsOpen || savesLibrary.IsOpen || mapsLibrary.IsOpen;
            library.Tick();
            savesLibrary.Tick();
            mapsLibrary.Tick();
            if (startVisible)
            {
                startScreen.Tick(!overlayOpen, startCanResume);
                return;
            }
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            bool question = keyboard.slashKey.wasPressedThisFrame && keyboard.shiftKey.isPressed;
            if (HelpOpen)
            {
                if (question || keyboard.escapeKey.wasPressedThisFrame)
                    SetHelp(false);
                return;
            }
            if (!Controller.KeyboardShortcutsEnabled)
                return;
            if (question)
                SetHelp(true);
            if (keyboard.tabKey.wasPressedThisFrame)
                ToggleExpanded();
            if (keyboard.lKey.wasPressedThisFrame)
                OpenLibrary();
            // Esc 逐层退出：先收起二级，再取消选区，最后退出盖章；关闭浮层的同一帧不再继续。
            if (keyboard.escapeKey.wasPressedThisFrame && !overlayOpen)
            {
                if (openParent != null)
                    CloseSub();
                else
                    Controller.Cancel();
            }
        }

        void LateUpdate()
        {
            UpdateScale();
            if (dock.panel == null)
                Build();
            LifeState state = Controller.State;
            LifePopulationSample populationSample = Controller.PopulationSample;
            long population = populationSample.Count;
            if (!shownPopulationSnapshot.Equals(populationSample.Snapshot))
            {
                shownPopulationSnapshot = populationSample.Snapshot;
                populationValue.tooltip = "第 " + populationSample.Snapshot.Generation + " 代快照人口；播放时不结算全图";
            }

            RefreshEditIcon();
            playItem.Element.EnableInClassList(OnClass, state.IsRunning);
            glider.Element.EnableInClassList(OnClass, state.IsRunning);

            if (state.IsRunning != shownRunning)
            {
                shownRunning = state.IsRunning;
                if (playToggle != null)
                {
                    playToggle.Icon.SetShape(shownRunning ? LifeIcons.Pause : LifeIcons.Play);
                    playToggle.Tip = shownRunning ? "暂停  Space" : "运行  Space";
                }
            }

            // 数字只在变化时更新；LifeNumber 逐位复用标签，不分配字符串。
            if (state.Generation != shownGeneration)
            {
                shownGeneration = state.Generation;
                generationValue.SetValue(shownGeneration);
                int phase = (int)(shownGeneration & 3);
                if (phase != shownPhase)
                {
                    shownPhase = phase;
                    glider.Icon.SetShape(LifeIcons.GliderPhases[phase]);
                }
            }
            if (population != shownPopulation)
            {
                shownPopulation = population;
                populationValue.SetValue(population);
            }

            RefreshSubStates();
            UpdateSelectionBar();

            if (toastHideTime > 0f && Time.unscaledTime >= toastHideTime)
            {
                toastHideTime = 0f;
                UpdateStatusVisibility();
            }
        }

        void OnDestroy()
        {
            Controller.Notified -= ShowToast;
            library?.Dispose();
            savesLibrary?.Dispose();
            mapsLibrary?.Dispose();
            startScreen?.Dispose();
            Destroy(font);
            Destroy(monoFont);
            Destroy(panelSettings);
        }
    }
}
