using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace GameOfLife
{
    /// <summary>
    /// 结构库：屏幕居中的固定浮层，与主界面同为方块图标、暗灰与白两种状态。
    /// 上为图标栏，中间是会动的缩略图网格，底部一行状态栏显示名称（悬停的或选中的）和对选中项的操作。
    /// 库内结构一视同仁，可存入、导入、改名、删除；具体存储由宿主通过委托提供。
    /// 预览用独立的小型托管模拟，只在浮层打开时运行，不影响正式世界。
    /// </summary>
    public sealed class LifePatternLibrary : IDisposable
    {
        const float StepInterval = 0.12f;
        const float DeleteConfirmSeconds = 3f;
        const int ResetGenerations = 120;
        const int AnimatedPopulationLimit = 2000;
        const int ThumbnailCanvas = 48;
        const string DefaultName = "新结构";
        const string OnClass = "life-dock__item--on";

        public bool IsOpen => backdrop.parent != null;

        /// <summary>选择结构并关闭浮层。</summary>
        public event Action<LifePattern> Picked;
        public event Action<bool> OpenChanged;

        /// <summary>把选区或整个棋盘存为指定名称的新结构；失败时返回 null。</summary>
        public Func<string, Task<LifePattern>> StoreRequested;
        /// <summary>从剪贴板导入；失败时返回 null。</summary>
        public Func<LifePattern> ImportRequested;
        /// <summary>改名并返回替换后的结构。</summary>
        public Func<LifePattern, string, LifePattern> RenameRequested;
        public Action<LifePattern> DeleteRequested;
        public Action<LifePattern> CopyRequested;
        public Action FolderRequested;
        /// <summary>主按钮的提示；结构库为盖章，存档与地图为载入。</summary>
        public string PickTip = "盖章  ↵";
        /// <summary>缩略图是否演化；地图列表项不是完整结构，不演化。</summary>
        public bool AnimatePreviews = true;
        /// <summary>预先生成的缩略图；返回 null 时按结构生成。纹理归提供方所有，浮层不销毁。</summary>
        public Func<LifePattern, Texture2D> PreviewImage;
        /// <summary>左上角的暗色标题，区分结构库、存档与地图。</summary>
        public string Title = string.Empty;

        readonly VisualElement root;
        readonly Func<IReadOnlyList<LifePattern>> patterns;
        readonly Func<LifePattern, bool> canDelete;
        readonly VisualElement backdrop;
        readonly TextField search;
        readonly ScrollView grid;
        readonly Label nameLabel;
        readonly TextField renameField;
        readonly VisualElement actions;
        readonly VisualElement deleteButton;
        readonly Label tip;
        readonly Label title;
        readonly Label empty;
        readonly VisualElement storeButton;
        readonly VisualElement importButton;
        readonly VisualElement folderButton;
        readonly VisualElement copyButton;
        readonly List<Tile> tiles = new List<Tile>();
        Tile selected;
        Tile deleteArmed;
        float deleteArmedUntil;
        bool renaming;
        float stepTimer;

        sealed class Tile
        {
            public LifePattern Pattern;
            public VisualElement Element;
            // 按结构生成的缩略图；使用预生成图片时为 null。
            public Preview Preview;
            public Texture2D Texture;
            public bool Visible = true;
        }

        public LifePatternLibrary(VisualElement root, Font monoFont, Func<IReadOnlyList<LifePattern>> patterns,
            Func<LifePattern, bool> canDelete)
        {
            this.root = root;
            this.patterns = patterns;
            this.canDelete = canDelete;
            FontDefinition mono = FontDefinition.FromFont(monoFont);

            backdrop = new VisualElement();
            backdrop.AddToClassList("life-lib-backdrop");
            backdrop.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == backdrop)
                    Close();
            });

            var window = Add(backdrop, new VisualElement(), "life-lib");

            // 顶部只有图标：搜索（展开输入框）、存入、导入、目录。关闭用 Esc 或点击浮层外。
            VisualElement header = Add(window, new VisualElement(), "life-lib__header");
            title = Add(header, new Label(), "life-lib__title");
            title.style.unityFontDefinition = mono;
            IconButton(header, LifeIcons.Search, "搜索  /", OpenSearch);
            search = Add(header, new TextField(), "life-lib__search");
            search.style.display = DisplayStyle.None;
            StyleCursor(search);
            search.RegisterValueChangedCallback(_ => ApplyFilter());
            Add(header, new VisualElement(), "life-lib__spacer");
            storeButton = IconButton(header, LifeIcons.Store, "存入选区，无选区时存入整个棋盘", StoreNew);
            importButton = IconButton(header, LifeIcons.Paste, "从剪贴板导入  Ctrl+V", Import);
            folderButton = IconButton(header, LifeIcons.Folder, "打开目录", () => FolderRequested?.Invoke());

            grid = Add(window, new ScrollView(ScrollViewMode.Vertical)
            {
                verticalScrollerVisibility = ScrollerVisibility.Hidden,
                horizontalScrollerVisibility = ScrollerVisibility.Hidden
            }, "life-lib__grid");
            // ScrollView 内部层级随版本变化，网格换行直接设在内容容器上。
            grid.contentContainer.style.flexDirection = FlexDirection.Row;
            grid.contentContainer.style.flexWrap = Wrap.Wrap;
            empty = Add(grid, new Label("空"), "life-lib__empty");
            empty.pickingMode = PickingMode.Ignore;

            // 底部状态栏：网格本身就是预览，这里只有名称与操作，不再重复显示大图。
            VisualElement footer = Add(window, new VisualElement(), "life-lib__footer");
            nameLabel = Add(footer, new Label(), "life-lib__name");
            nameLabel.style.unityFontDefinition = mono;
            nameLabel.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (selected != null && RenameRequested != null && canDelete(selected.Pattern))
                    ShowTip(nameLabel, "双击改名  F2");
            });
            nameLabel.RegisterCallback<PointerLeaveEvent>(_ => HideTip());
            nameLabel.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount >= 2)
                    BeginRename();
            });
            renameField = Add(footer, new TextField(), "life-lib__rename");
            renameField.style.display = DisplayStyle.None;
            StyleCursor(renameField);
            renameField.RegisterCallback<FocusOutEvent>(_ => EndRename(true));
            Add(footer, new VisualElement(), "life-lib__spacer");
            actions = Add(footer, new VisualElement(), "life-lib__actions");
            IconButton(actions, LifeIcons.Use, () => PickTip, () => Pick(selected)).AddToClassList(OnClass);
            copyButton = IconButton(actions, LifeIcons.Copy, "复制 RLE", () => { if (selected != null) CopyRequested?.Invoke(selected.Pattern); });
            deleteButton = IconButton(actions, LifeIcons.Delete, "删除  Del", RequestDelete);

            // 提示最后加入，盖在网格与按钮之上。
            tip = Add(backdrop, new Label(), "life-lib__tip");
            tip.pickingMode = PickingMode.Ignore;
            tip.style.unityFontDefinition = mono;
            tip.style.display = DisplayStyle.None;
        }

        VisualElement IconButton(VisualElement parent, string[] shape, string tipText, Action click)
            => IconButton(parent, shape, () => tipText, click);

        VisualElement IconButton(VisualElement parent, string[] shape, Func<string> tipText, Action click)
        {
            VisualElement item = Add(parent, new VisualElement(), "life-dock__item");
            Add(item, new LifeCellIcon(), "life-dock__icon").SetShape(shape);
            item.RegisterCallback<PointerEnterEvent>(_ => ShowTip(item, tipText()));
            item.RegisterCallback<PointerLeaveEvent>(_ => HideTip());
            item.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button == 0)
                    click();
            });
            return item;
        }

        void ShowTip(VisualElement anchor, string text)
        {
            // 提示放在上方，避免被鼠标光标挡住；浮层铺满面板，按底边距离定位。
            tip.text = text;
            Rect bound = anchor.worldBound;
            tip.style.left = bound.x;
            tip.style.bottom = backdrop.layout.height - bound.y + 4f;
            tip.style.display = DisplayStyle.Flex;
        }

        void HideTip() => tip.style.display = DisplayStyle.None;

        void OpenSearch()
        {
            search.style.display = DisplayStyle.Flex;
            search.schedule.Execute(() => search.Focus());
        }

        void CloseSearch()
        {
            search.SetValueWithoutNotify(string.Empty);
            search.Blur();
            search.style.display = DisplayStyle.None;
            ApplyFilter();
        }

        static T Add<T>(VisualElement parent, T element, string className) where T : VisualElement
        {
            element.AddToClassList(className);
            parent.Add(element);
            return element;
        }

        static void StyleCursor(TextField field)
        {
            field.textSelection.cursorColor = Color.white;
            field.textSelection.selectionColor = new Color(1f, 1f, 1f, 0.3f);
        }

        public void Open(LifePattern current)
        {
            if (!IsOpen)
            {
                root.Add(backdrop);
                OpenChanged?.Invoke(true);
            }
            search.SetValueWithoutNotify(string.Empty);
            search.style.display = DisplayStyle.None;
            title.text = Title;
            title.style.display = string.IsNullOrEmpty(Title) ? DisplayStyle.None : DisplayStyle.Flex;
            // 只显示宿主提供了处理的操作：存档与地图没有导入和目录，地图也不能存入、复制以外的修改。
            storeButton.style.display = StoreRequested != null ? DisplayStyle.Flex : DisplayStyle.None;
            importButton.style.display = ImportRequested != null ? DisplayStyle.Flex : DisplayStyle.None;
            folderButton.style.display = FolderRequested != null ? DisplayStyle.Flex : DisplayStyle.None;
            copyButton.style.display = CopyRequested != null ? DisplayStyle.Flex : DisplayStyle.None;
            Refresh(current);
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            EndRename(true);
            search.Blur();
            HideTip();
            backdrop.RemoveFromHierarchy();
            OpenChanged?.Invoke(false);
        }

        /// <summary>结构列表变化后重建网格，尽量保持原选中项。</summary>
        public void Refresh(LifePattern current = null)
        {
            current ??= selected?.Pattern;
            // 缩略图按结构对象复用：大型结构生成缩略图代价高，增删改只为新对象生成。
            var reusable = new Dictionary<LifePattern, Preview>(tiles.Count);
            foreach (Tile tile in tiles)
                if (tile.Preview != null)
                    reusable[tile.Pattern] = tile.Preview;
            tiles.Clear();
            grid.Clear();
            grid.Add(empty);
            selected = null;
            Disarm();
            foreach (LifePattern pattern in patterns())
            {
                Texture2D image = PreviewImage?.Invoke(pattern);
                Preview thumbnail = null;
                if (image == null && !reusable.Remove(pattern, out thumbnail))
                    thumbnail = new Preview(pattern, ThumbnailCanvas, AnimatePreviews);
                var tile = new Tile { Pattern = pattern, Preview = thumbnail, Texture = image ?? thumbnail.Texture };
                tile.Element = CreateTileElement(tile);
                grid.Add(tile.Element);
                tiles.Add(tile);
                if (pattern == current)
                    selected = tile;
            }
            foreach (Preview stale in reusable.Values)
                stale.Dispose();
            ApplyFilter();
        }

        VisualElement CreateTileElement(Tile tile)
        {
            var element = new VisualElement();
            element.AddToClassList("life-lib__tile");
            VisualElement thumb = Add(element, new VisualElement(), "life-lib__thumb");
            thumb.style.backgroundImage = tile.Texture;
            thumb.pickingMode = PickingMode.Ignore;
            // 悬停时状态栏临时显示该结构的名称，移开后回到选中项。
            element.RegisterCallback<PointerEnterEvent>(_ => ShowName(tile));
            element.RegisterCallback<PointerLeaveEvent>(_ => ShowName(selected));
            element.RegisterCallback<ClickEvent>(evt =>
            {
                Select(tile);
                if (evt.clickCount >= 2)
                    Pick(tile);
            });
            return element;
        }

        void ApplyFilter()
        {
            string filter = search.value.Trim();
            int visible = 0;
            Tile first = null;
            foreach (Tile tile in tiles)
            {
                tile.Visible = filter.Length == 0
                    || tile.Pattern.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                tile.Element.style.display = tile.Visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!tile.Visible)
                    continue;
                visible++;
                first ??= tile;
            }
            empty.style.display = visible == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            Select(selected != null && selected.Visible ? selected : first);
        }

        void Select(Tile tile)
        {
            if (tile != selected)
            {
                EndRename(true);
                Disarm();
            }
            if (selected != null)
                selected.Element.RemoveFromClassList("life-lib__tile--current");
            selected = tile;
            ShowName(tile);
            if (tile == null)
            {
                actions.style.visibility = Visibility.Hidden;
                return;
            }

            tile.Element.AddToClassList("life-lib__tile--current");
            grid.ScrollTo(tile.Element);
            actions.style.visibility = Visibility.Visible;
            deleteButton.style.display = canDelete(tile.Pattern) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ShowName(Tile tile)
        {
            if (!renaming)
                nameLabel.text = tile?.Pattern.Name ?? string.Empty;
        }

        void Pick(Tile tile)
        {
            if (tile == null)
                return;
            Close();
            Picked?.Invoke(tile.Pattern);
        }

        // ---------- 增、删、改 ----------

        /// <summary>存入选区（无选区时为整个棋盘）并进入改名；选区操作条的“存入”也走这里。</summary>
        public async void StoreNew()
        {
            storeButton.SetEnabled(false);
            LifePattern pattern;
            try
            {
                pattern = await StoreRequested(DefaultName);
            }
            finally
            {
                storeButton.SetEnabled(true);
            }
            if (pattern == null)
                return;
            search.SetValueWithoutNotify(string.Empty);
            Refresh(pattern);
            // 新结构以默认名存入，立即进入改名，回车确认。
            BeginRename();
        }

        void Import()
        {
            LifePattern pattern = ImportRequested?.Invoke();
            if (pattern == null)
                return;
            search.SetValueWithoutNotify(string.Empty);
            Refresh(pattern);
        }

        void BeginRename()
        {
            if (selected == null || renaming || RenameRequested == null || !canDelete(selected.Pattern))
                return;
            renaming = true;
            renameField.SetValueWithoutNotify(selected.Pattern.Name);
            nameLabel.style.display = DisplayStyle.None;
            renameField.style.display = DisplayStyle.Flex;
            renameField.schedule.Execute(() =>
            {
                renameField.Focus();
                renameField.SelectAll();
            });
        }

        void EndRename(bool commit)
        {
            if (!renaming)
                return;
            renaming = false;
            renameField.style.display = DisplayStyle.None;
            nameLabel.style.display = DisplayStyle.Flex;
            renameField.Blur();
            string name = renameField.value.Trim();
            if (!commit || selected == null || name.Length == 0 || name == selected.Pattern.Name)
                return;
            LifePattern renamed = RenameRequested?.Invoke(selected.Pattern, name);
            if (renamed != null)
                Refresh(renamed);
        }

        /// <summary>两步删除：第一次只把按钮变为确认，3 秒内再次触发才删除文件。</summary>
        void RequestDelete()
        {
            if (selected == null || !canDelete(selected.Pattern))
                return;
            if (deleteArmed == selected && Time.unscaledTime <= deleteArmedUntil)
            {
                LifePattern pattern = selected.Pattern;
                int index = tiles.IndexOf(selected);
                Disarm();
                DeleteRequested?.Invoke(pattern);
                IReadOnlyList<LifePattern> remaining = patterns();
                Refresh(remaining.Count == 0 ? null : remaining[math.min(index, remaining.Count - 1)]);
                return;
            }
            deleteArmed = selected;
            deleteArmedUntil = Time.unscaledTime + DeleteConfirmSeconds;
            deleteButton.AddToClassList(OnClass);
            ShowTip(deleteButton, "再点一次删除");
        }

        void Disarm()
        {
            if (deleteArmed != null)
                HideTip();
            deleteArmed = null;
            deleteButton.RemoveFromClassList(OnClass);
        }

        // ---------- 每帧 ----------

        /// <summary>由宿主每帧调用：处理键盘并推进可见缩略图的演化。</summary>
        public void Tick()
        {
            if (!IsOpen)
                return;
            HandleKeyboard();
            if (deleteArmed != null && Time.unscaledTime > deleteArmedUntil)
                Disarm();

            stepTimer += Time.unscaledDeltaTime;
            if (stepTimer < StepInterval)
                return;
            stepTimer = 0f;
            foreach (Tile tile in tiles)
                if (tile.Visible)
                    tile.Preview?.Step();
        }

        bool IsFocused(TextField field)
            => field.panel?.focusController?.focusedElement is VisualElement focused
                && (focused == field || field.Contains(focused));

        void HandleKeyboard()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;
            bool enter = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;

            if (renaming)
            {
                if (enter)
                    EndRename(true);
                else if (keyboard.escapeKey.wasPressedThisFrame)
                    EndRename(false);
                return;
            }

            bool typing = IsFocused(search);
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                if (search.style.display == DisplayStyle.Flex)
                    CloseSearch();
                else
                    Close();
                return;
            }
            if (enter)
            {
                Pick(selected);
                return;
            }
            if (typing)
            {
                if (keyboard.downArrowKey.wasPressedThisFrame)
                    Move(Columns());
                if (keyboard.upArrowKey.wasPressedThisFrame)
                    Move(-Columns());
                return;
            }

            if (keyboard.slashKey.wasPressedThisFrame && !keyboard.shiftKey.isPressed)
            {
                OpenSearch();
                return;
            }
            if (keyboard.f2Key.wasPressedThisFrame)
            {
                BeginRename();
                return;
            }
            if (keyboard.deleteKey.wasPressedThisFrame)
            {
                RequestDelete();
                return;
            }
            if (keyboard.ctrlKey.isPressed && keyboard.vKey.wasPressedThisFrame)
            {
                Import();
                return;
            }

            int columns = Columns();
            if (keyboard.downArrowKey.wasPressedThisFrame)
                Move(columns);
            if (keyboard.upArrowKey.wasPressedThisFrame)
                Move(-columns);
            if (keyboard.rightArrowKey.wasPressedThisFrame)
                Move(1);
            if (keyboard.leftArrowKey.wasPressedThisFrame)
                Move(-1);
        }

        int Columns()
        {
            Tile sample = tiles.Find(tile => tile.Visible);
            if (sample == null)
                return 1;
            float tileWidth = sample.Element.layout.width + sample.Element.resolvedStyle.marginLeft
                + sample.Element.resolvedStyle.marginRight;
            return tileWidth <= 0f ? 1 : math.max(1, (int)(grid.contentContainer.layout.width / tileWidth));
        }

        void Move(int offset)
        {
            var visible = tiles.FindAll(tile => tile.Visible);
            if (visible.Count == 0)
                return;
            int index = math.max(0, visible.IndexOf(selected));
            Select(visible[math.clamp(index + offset, 0, visible.Count - 1)]);
        }

        void DisposeTextures()
        {
            foreach (Tile tile in tiles)
                tile.Preview?.Dispose();
        }

        public void Dispose()
        {
            Close();
            DisposeTextures();
            tiles.Clear();
        }

        /// <summary>
        /// 单个结构的循环演化预览：画布为包围盒加边距，越出画布的细胞丢弃（枪发射的滑翔机飞出即消失），
        /// 结构消亡或演化够久后回到初始状态。人口过大的结构只显示静态图。
        /// </summary>
        sealed class Preview : IDisposable
        {
            public Texture2D Texture { get; }
            public int2 Size { get; }

            readonly int2[] seed;
            readonly int2 origin;
            readonly int2 canvas;
            readonly int scale;
            readonly bool animated;
            readonly Color32[] pixels;
            HashSet<int2> cells;
            HashSet<int2> next;
            readonly Dictionary<int2, int> neighbours;
            int age;

            public Preview(LifePattern pattern, int canvasCells, bool animate)
            {
                using var bitmap = new LifePatternBitmap(pattern.Tiles);
                int2 minimum = bitmap.Minimum;
                Size = bitmap.Maximum - minimum + 1;
                int2 size = math.max(Size, 1);

                // 小结构居中放进至少 canvasCells / 4 见方的画布，四周留出演化空间。
                int margin = math.max(3, math.cmax(size) / 5);
                canvas = math.max(size + 2 * margin, canvasCells / 4);
                origin = minimum - (canvas - size) / 2;
                scale = math.max(1, (math.cmax(canvas) + canvasCells - 1) / canvasCells);
                int2 texel = (canvas + scale - 1) / scale;

                animated = animate && pattern.Population <= AnimatedPopulationLimit;
                Texture = new Texture2D(texel.x, texel.y, TextureFormat.RGBA32, false)
                {
                    name = "Library preview " + pattern.Name,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                bitmap.Draw(Texture, origin, canvas, scale);
                if (animated)
                {
                    seed = Expand(pattern);
                    cells = new HashSet<int2>(seed);
                    next = new HashSet<int2>();
                    neighbours = new Dictionary<int2, int>();
                    pixels = new Color32[texel.x * texel.y];
                }
            }

            static int2[] Expand(LifePattern pattern)
            {
                LifeTile[] tiles = pattern.Tiles;
                var result = new int2[(int)pattern.Population];
                int index = 0;
                for (int i = 0; i < tiles.Length; i++)
                {
                    LifeTile tile = tiles[i];
                    for (int row = 0; row < 16; row++)
                    {
                        uint bits = tile.Bits.GetRow(row);
                        while (bits != 0)
                        {
                            int column = math.tzcnt(bits);
                            bits &= bits - 1;
                            int2 cell = tile.Position + new int2(column, row);
                            result[index++] = cell;
                        }
                    }
                }
                return result;
            }

            public void Step()
            {
                if (!animated)
                    return;
                if (cells.Count == 0 || ++age > ResetGenerations)
                {
                    cells.Clear();
                    cells.UnionWith(seed);
                    age = 0;
                    Render();
                    return;
                }

                neighbours.Clear();
                foreach (int2 cell in cells)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;
                            int2 key = cell + new int2(dx, dy);
                            neighbours.TryGetValue(key, out int n);
                            neighbours[key] = n + 1;
                        }

                next.Clear();
                foreach (KeyValuePair<int2, int> pair in neighbours)
                {
                    int2 local = pair.Key - origin;
                    if (math.any(local < 0) || math.any(local >= canvas))
                        continue;
                    if (pair.Value == 3 || pair.Value == 2 && cells.Contains(pair.Key))
                        next.Add(pair.Key);
                }
                (cells, next) = (next, cells);
                Render();
            }

            void Render()
            {
                int width = Texture.width;
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(0, 0, 0, 0);
                foreach (int2 cell in cells)
                {
                    int2 local = cell - origin;
                    if (math.any(local < 0) || math.any(local >= canvas))
                        continue;
                    int2 p = local / scale;
                    pixels[p.y * width + p.x] = new Color32(255, 255, 255, 255);
                }
                Texture.SetPixels32(pixels);
                Texture.Apply(false, false);
            }

            public void Dispose() => UnityEngine.Object.Destroy(Texture);
        }
    }
}
