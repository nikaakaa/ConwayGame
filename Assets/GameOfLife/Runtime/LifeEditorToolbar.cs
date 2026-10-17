using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace GameOfLife
{
    [DefaultExecutionOrder(100)]
    public sealed class LifeEditorToolbar : MonoBehaviour
    {
        public LifeController Controller;
        public LifeView View;

        const string OnClass = "life-button--on";
        const string AliveClass = "life-glider-cell--alive";

        // 滑翔机连续四代在 3×3 内的形状，行优先。
        static readonly string[] GliderPhases = { ".X...XXXX", "X.X.XX.X.", "..XX.X.XX", "X...XXXX." };

        VisualElement toolbar;
        VisualElement chip;
        PopupField<LifePattern> presets;
        FloatField speed;
        TextField patternName;
        Button cellBrush;
        Button patternBrush;
        Button selectTool;
        Button delete;
        Button run;
        Button save;
        Label generationValue;
        Label populationValue;
        Label toast;
        readonly List<VisualElement[]> gliders = new List<VisualElement[]>();
        Font font;
        Font monoFont;
        bool editingSpeed;
        long shownGeneration = -1;
        int shownPopulation = -1;
        int shownPhase = -1;
        float toastHideTime;

        void Start()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
            monoFont = Font.CreateDynamicFontFromOSFont("Consolas", 14);
            root.style.unityFontDefinition = FontDefinition.FromFont(font);

            toolbar = Add(root, new VisualElement(), "life-toolbar");

            // 头部只有滑翔机、代数和细胞数，含义由图形表达。
            VisualElement header = Add(toolbar, new VisualElement(), "life-header");
            header.Add(Glider());
            generationValue = Mono(header, "life-generation");
            generationValue.tooltip = "代数";
            Add(header, new VisualElement(), "life-spacer");
            Add(header, new VisualElement(), "life-population-cell");
            populationValue = Mono(header, "life-population");
            populationValue.tooltip = "活细胞";
            Add(header, new Button(ToggleToolbar) { text = "—", tooltip = "Tab", focusable = false }, "life-button--square");

            VisualElement brushes = Row();
            cellBrush = AddButton(brushes, "单格", Controller.UseCellBrush);
            patternBrush = AddButton(brushes, "盖章", () => Controller.SelectPattern(Controller.SelectedPattern));
            selectTool = AddButton(brushes, "框选", Controller.UseSelectTool);
            selectTool.tooltip = "S";
            presets = new PopupField<LifePattern>(new List<LifePattern>(Controller.Store.Patterns),
                Controller.SelectedPattern, pattern => pattern.Name, pattern => pattern.Name);
            presets.focusable = false;
            presets.style.flexGrow = 1;
            presets.style.marginRight = 0;
            presets.RegisterValueChangedCallback(change => Controller.SelectPattern(change.newValue));
            brushes.Add(presets);

            VisualElement playback = Row();
            run = AddButton(playback, "运行", Controller.ToggleRunning, true);
            AddButton(playback, "单步", Controller.Step, true);
            AddButton(playback, "清空", Controller.Clear, true);
            AddButton(playback, "重置", Controller.ResetPattern, true);

            VisualElement timing = Row();
            AddButton(timing, "÷2", () => Controller.ChangeSpeed(0.5f)).AddToClassList("life-button--square");
            speed = new FloatField { value = Controller.Speed, isDelayed = true, formatString = "0", tooltip = "代/秒" };
            speed.AddToClassList("life-speed");
            speed.Q(className: "unity-base-field__input").style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            speed.RegisterValueChangedCallback(change => Controller.SetSpeed(change.newValue));
            speed.RegisterCallback<FocusInEvent>(_ => { editingSpeed = true; Controller.KeyboardShortcutsEnabled = false; });
            speed.RegisterCallback<FocusOutEvent>(_ => { editingSpeed = false; Controller.KeyboardShortcutsEnabled = true; });
            StyleSelection(speed.textSelection);
            timing.Add(speed);
            AddButton(timing, "×2", () => Controller.ChangeSpeed(2f)).AddToClassList("life-button--square");
            Add(timing, new VisualElement(), "life-spacer");
            AddButton(timing, "−", () => Zoom(false)).AddToClassList("life-button--square");
            AddButton(timing, "+", () => Zoom(true)).AddToClassList("life-button--square");

            VisualElement storage = Row();
            patternName = new TextField { value = Controller.PatternName };
            patternName.style.flexGrow = 1;
            patternName.RegisterValueChangedCallback(change => Controller.PatternName = change.newValue);
            patternName.RegisterCallback<FocusInEvent>(_ => Controller.KeyboardShortcutsEnabled = false);
            patternName.RegisterCallback<FocusOutEvent>(_ => Controller.KeyboardShortcutsEnabled = true);
            StyleSelection(patternName.textSelection);
            storage.Add(patternName);
            save = AddButton(storage, "保存", Controller.SaveCells);
            save.tooltip = "有选区时只保存选区";
            AddButton(storage, "刷新", Controller.ReloadPatterns);
            AddButton(storage, "目录", () => Application.OpenURL(new Uri(Controller.PatternsDirectory).AbsoluteUri));

            VisualElement clipboard = Row();
            AddButton(clipboard, "复制", Controller.CopyToClipboard, true).tooltip = "Ctrl+C · RLE";
            AddButton(clipboard, "导入", Controller.PasteFromClipboard, true).tooltip = "Ctrl+V · RLE / Plaintext / JSON";
            delete = AddButton(clipboard, "删除", Controller.DeleteSelection, true);
            delete.tooltip = "Delete";

            toast = Add(toolbar, new Label(), "life-toast");
            toast.style.display = DisplayStyle.None;
            Controller.Notified += ShowToast;
            Controller.PatternsChanged += RefreshPatterns;

            chip = Add(root, Glider(), "life-chip");
            chip.tooltip = "Tab";
            chip.style.display = DisplayStyle.None;
            chip.RegisterCallback<ClickEvent>(_ => ToggleToolbar());
        }

        static T Add<T>(VisualElement parent, T element, string className) where T : VisualElement
        {
            element.AddToClassList(className);
            parent.Add(element);
            return element;
        }

        VisualElement Row() => Add(toolbar, new VisualElement(), "life-row");

        Label Mono(VisualElement parent, string className)
        {
            Label label = Add(parent, new Label("0"), className);
            label.style.unityFontDefinition = FontDefinition.FromFont(monoFont);
            return label;
        }

        VisualElement Glider()
        {
            var glider = new VisualElement();
            glider.AddToClassList("life-glider");
            glider.pickingMode = PickingMode.Ignore;
            var cells = new VisualElement[9];
            for (int row = 0; row < 3; row++)
            {
                VisualElement line = Add(glider, new VisualElement(), "life-glider-row");
                line.pickingMode = PickingMode.Ignore;
                for (int column = 0; column < 3; column++)
                {
                    VisualElement cell = Add(line, new VisualElement(), "life-glider-cell");
                    cell.pickingMode = PickingMode.Ignore;
                    cells[row * 3 + column] = cell;
                }
            }
            gliders.Add(cells);
            return glider;
        }

        static void StyleSelection(ITextSelection selection)
        {
            selection.cursorColor = Color.white;
            selection.selectionColor = new Color(1f, 1f, 1f, 0.3f);
        }

        static Button AddButton(VisualElement parent, string text, Action action, bool grow = false)
        {
            // 按钮不取焦点，Space 等快捷键始终交给控制器。
            var button = new Button(action) { text = text, focusable = false };
            if (grow)
                button.AddToClassList("life-button--grow");
            parent.Add(button);
            return button;
        }

        void Update()
        {
            if (Controller.KeyboardShortcutsEnabled && Keyboard.current.tabKey.wasPressedThisFrame)
                ToggleToolbar();
        }

        void LateUpdate()
        {
            LifeState state = Controller.State;
            int population = Controller.Cells.Length;

            cellBrush.EnableInClassList(OnClass, Controller.Brush == LifeBrushMode.Cell);
            patternBrush.EnableInClassList(OnClass, Controller.Brush == LifeBrushMode.Pattern);
            selectTool.EnableInClassList(OnClass, Controller.Brush == LifeBrushMode.Select);
            delete.SetEnabled(Controller.HasSelection);
            run.EnableInClassList(OnClass, state.IsRunning);
            presets.SetValueWithoutNotify(Controller.SelectedPattern);
            run.text = state.IsRunning ? "暂停" : "运行";
            save.SetEnabled(population != 0 && patternName.value.Length != 0);
            if (!editingSpeed)
                speed.SetValueWithoutNotify(Controller.Speed);

            // 数字只在变化时重建字符串，避免每帧分配。
            if (state.Generation != shownGeneration)
            {
                shownGeneration = state.Generation;
                generationValue.text = shownGeneration.ToString();
            }
            if (population != shownPopulation)
            {
                shownPopulation = population;
                populationValue.text = population.ToString();
            }

            int phase = (int)(state.Generation & 3);
            if (phase != shownPhase)
            {
                shownPhase = phase;
                string shape = GliderPhases[phase];
                foreach (VisualElement[] cells in gliders)
                    for (int i = 0; i < cells.Length; i++)
                        cells[i].EnableInClassList(AliveClass, shape[i] == 'X');
            }

            if (toastHideTime > 0f && Time.unscaledTime >= toastHideTime)
            {
                toastHideTime = 0f;
                toast.style.display = DisplayStyle.None;
            }
        }

        void ToggleToolbar()
        {
            bool show = toolbar.resolvedStyle.display == DisplayStyle.None;
            toolbar.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            chip.style.display = show ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void Zoom(bool zoomIn)
        {
            View.Zoom(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), null, zoomIn);
        }

        void ShowToast(string message)
        {
            toast.text = message;
            toast.style.display = DisplayStyle.Flex;
            toastHideTime = Time.unscaledTime + 2f;
        }

        void RefreshPatterns()
        {
            presets.choices = new List<LifePattern>(Controller.Store.Patterns);
        }

        void OnDestroy()
        {
            Controller.Notified -= ShowToast;
            Controller.PatternsChanged -= RefreshPatterns;
            Destroy(font);
            Destroy(monoFont);
        }
    }
}
