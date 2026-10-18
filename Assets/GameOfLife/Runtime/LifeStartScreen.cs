using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace GameOfLife
{
    /// <summary>
    /// 开始界面：背景是一门 Gosper 枪在暗处持续演化，中间为滑翔机标志与菜单。
    /// 菜单为新世界、继续（有当前世界或存档时）、存档、地图、退出程序；鼠标或方向键加回车选择。
    /// 背景使用独立的定长网格模拟，越出边界的细胞直接丢弃，不影响正式世界。
    /// 载入存档或地图时菜单换成一行“载入中”，滑翔机继续飞行；同步编译期间画面停在最后一帧。
    /// </summary>
    public sealed class LifeStartScreen
    {
        const float StepInterval = 0.08f;
        const int Width = 192;
        const int Height = 108;
        const string GunRle = "24bo$22bobo$12b2o6b2o12b2o$11bo3bo4b2o12b2o$2o8bo5bo3b2o$2o8bo3bob2o4bobo$10bo5bo7bo$11bo3bo$12b2o!";

        public bool IsLoading { get; private set; }

        public event Action NewWorldRequested;
        public event Action ContinueRequested;
        public event Action SavesRequested;
        public event Action MapsRequested;
        public event Action QuitRequested;

        readonly VisualElement root;
        readonly VisualElement overlay;
        readonly VisualElement menu;
        readonly LifeCellIcon logo;
        readonly Label loading;
        readonly Texture2D texture;
        readonly Color32[] pixels = new Color32[Width * Height];
        bool[] cells = new bool[Width * Height];
        bool[] next = new bool[Width * Height];
        readonly List<(string Label, Action Action)> items = new List<(string, Action)>();
        readonly List<Label> labels = new List<Label>();
        int selected;
        float stepTimer;
        int generation;

        public LifeStartScreen(VisualElement root, Font monoFont)
        {
            this.root = root;
            overlay = new VisualElement();
            overlay.AddToClassList("life-start");

            texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = "Start screen life",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var background = new VisualElement { pickingMode = PickingMode.Ignore };
            background.AddToClassList("life-start__background");
            background.style.backgroundImage = texture;
            overlay.Add(background);

            var center = new VisualElement { pickingMode = PickingMode.Ignore };
            center.AddToClassList("life-start__center");
            overlay.Add(center);
            logo = new LifeCellIcon();
            logo.AddToClassList("life-start__logo");
            logo.SetShape(LifeIcons.GliderPhases[0]);
            center.Add(logo);
            menu = new VisualElement();
            menu.AddToClassList("life-start__menu");
            menu.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            center.Add(menu);
            loading = new Label("载入中");
            loading.AddToClassList("life-start__loading");
            loading.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            loading.style.display = DisplayStyle.None;
            center.Add(loading);

            SeedBackground();
        }

        /// <summary>显示开始界面。</summary>
        /// <param name="canContinue">有当前世界或存档时提供“继续”。</param>
        public void Show(bool canContinue)
        {
            items.Clear();
            items.Add(("新世界", () => NewWorldRequested?.Invoke()));
            if (canContinue)
                items.Add(("继续", () => ContinueRequested?.Invoke()));
            items.Add(("存档", () => SavesRequested?.Invoke()));
            items.Add(("地图", () => MapsRequested?.Invoke()));
            items.Add(("退出程序", () => QuitRequested?.Invoke()));

            menu.Clear();
            labels.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var label = new Label(items[i].Label);
                label.AddToClassList("life-start__item");
                label.RegisterCallback<PointerEnterEvent>(_ => Select(index));
                label.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button == 0)
                        items[index].Action();
                });
                menu.Add(label);
                labels.Add(label);
            }
            Select(0);
            root.Add(overlay);
            overlay.BringToFront();
        }

        /// <summary>菜单换成一行提示并停止接收输入，盖在存档与地图浮层之上。</summary>
        public void ShowLoading(string text = "载入中")
        {
            IsLoading = true;
            loading.text = text;
            menu.style.display = DisplayStyle.None;
            loading.style.display = DisplayStyle.Flex;
            overlay.BringToFront();
        }

        public void HideLoading()
        {
            IsLoading = false;
            menu.style.display = DisplayStyle.Flex;
            loading.style.display = DisplayStyle.None;
        }

        void Select(int index)
        {
            selected = index;
            for (int i = 0; i < labels.Count; i++)
                labels[i].EnableInClassList("life-start__item--on", i == selected);
        }

        /// <summary>由宿主每帧调用；acceptInput 为 false 时只推进背景，resumeWithEscape 决定 Esc 是否继续当前世界。</summary>
        public void Tick(bool acceptInput, bool resumeWithEscape)
        {
            if (acceptInput && !IsLoading)
                HandleKeyboard(resumeWithEscape);

            stepTimer += Time.unscaledDeltaTime;
            if (stepTimer < StepInterval)
                return;
            stepTimer = 0f;
            StepBackground();
            if (++generation % 3 == 0)
                logo.SetShape(LifeIcons.GliderPhases[generation / 3 & 3]);
        }

        void HandleKeyboard(bool resumeWithEscape)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || items.Count == 0)
                return;
            if (keyboard.downArrowKey.wasPressedThisFrame)
                Select((selected + 1) % items.Count);
            if (keyboard.upArrowKey.wasPressedThisFrame)
                Select((selected + items.Count - 1) % items.Count);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                items[selected].Action();
            else if (keyboard.escapeKey.wasPressedThisFrame && resumeWithEscape)
                ContinueRequested?.Invoke();
        }

        void SeedBackground()
        {
            Array.Clear(cells, 0, cells.Length);
            // 枪放在左上方，滑翔机向右下飞出画面。
            int x = 0;
            int y = 0;
            int count = 0;
            foreach (char c in GunRle)
            {
                if (char.IsDigit(c))
                {
                    count = count * 10 + (c - '0');
                    continue;
                }
                int run = count == 0 ? 1 : count;
                count = 0;
                if (c == 'b')
                    x += run;
                else if (c == 'o')
                {
                    for (int i = 0; i < run; i++)
                        Set(12 + x + i, 10 + y);
                    x += run;
                }
                else if (c == '$')
                {
                    y += run;
                    x = 0;
                }
            }
            Render();
        }

        void Set(int x, int y)
        {
            if (x >= 0 && y >= 0 && x < Width && y < Height)
                cells[y * Width + x] = true;
        }

        void StepBackground()
        {
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int neighbours = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= Height)
                            continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if ((dx != 0 || dy != 0) && nx >= 0 && nx < Width && cells[ny * Width + nx])
                                neighbours++;
                        }
                    }
                    bool alive = cells[y * Width + x];
                    next[y * Width + x] = neighbours == 3 || alive && neighbours == 2;
                }
            (cells, next) = (next, cells);
            Render();
        }

        void Render()
        {
            // 纹理第一行在底部，网格第一行在顶部，绘制时翻转。
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    pixels[(Height - 1 - y) * Width + x] = cells[y * Width + x]
                        ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        public void Dispose()
        {
            overlay.RemoveFromHierarchy();
            UnityEngine.Object.Destroy(texture);
        }
    }
}
