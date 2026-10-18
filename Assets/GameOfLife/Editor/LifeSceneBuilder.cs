using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace GameOfLife.Editor
{
    public static class LifeSceneBuilder
    {
        const string ScenePath = "Assets/GameOfLife/Scenes/GameOfLife.unity";
        const string MainMenuPath = "Assets/GameOfLife/Scenes/MainMenu.unity";
        const string PanelSettingsPath = "Assets/GameOfLife/UI/LifeEditorPanel.asset";
        const string ThemePath = "Assets/GameOfLife/UI/LifeEditorTheme.tss";
        const string InputActionsPath = "Assets/InputSystem_Actions.inputactions";

        [MenuItem("Tools/Game of Life/Create Demo Scenes")]
        public static void CreateDemoScenes()
        {
            Directory.CreateDirectory("Assets/GameOfLife/Scenes");
            CreatePanelSettings();
            var menu = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateCamera();
            CreateEventSystem();
            var menuObject = new GameObject("Main Menu", typeof(UIDocument), typeof(LifeMainMenu));
            menuObject.GetComponent<UIDocument>().panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            EditorSceneManager.SaveScene(menu, MainMenuPath);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateCamera();
            CreateEventSystem();

            var canvasObject = new GameObject("Life Canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            var boardObject = new GameObject("Board", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            boardObject.transform.SetParent(canvasObject.transform, false);
            RectTransform boardRect = boardObject.GetComponent<RectTransform>();
            boardRect.anchorMin = new Vector2(0.5f, 0.5f);
            boardRect.anchorMax = new Vector2(0.5f, 0.5f);
            boardRect.pivot = new Vector2(0.5f, 0.5f);
            AspectRatioFitter fitter = boardObject.GetComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = 1;

            LifeView view = boardObject.AddComponent<LifeView>();
            view.BoardImage = boardObject.GetComponent<RawImage>();
            LifeController controller = boardObject.AddComponent<LifeController>();
            controller.View = view;

            var editorObject = new GameObject("Life Editor", typeof(UIDocument));
            editorObject.GetComponent<UIDocument>().panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            LifeEditorToolbar toolbar = editorObject.AddComponent<LifeEditorToolbar>();
            toolbar.Controller = controller;
            toolbar.View = view;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MainMenuPath, true),
                new EditorBuildSettingsScene(ScenePath, true)
            };
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(MainMenuPath, OpenSceneMode.Single);
            EditorApplication.ExecuteMenuItem("File/Save Project");
            Debug.Log("Game of Life 主菜单与游戏场景已保存");
        }

        static void CreateCamera()
        {
            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(UniversalAdditionalCameraData));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0, 0, -10);
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;
        }

        static void CreateEventSystem()
        {
            var eventSystem = new GameObject("EventSystem");
            eventSystem.SetActive(false);
            eventSystem.AddComponent<EventSystem>();
            InputSystemUIInputModule input = eventSystem.AddComponent<InputSystemUIInputModule>();
            InputActionAsset actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
            input.point = InputActionReference.Create(actions.FindAction("UI/Point", true));
            input.leftClick = InputActionReference.Create(actions.FindAction("UI/Click", true));
            input.rightClick = InputActionReference.Create(actions.FindAction("UI/RightClick", true));
            input.middleClick = InputActionReference.Create(actions.FindAction("UI/MiddleClick", true));
            input.scrollWheel = InputActionReference.Create(actions.FindAction("UI/ScrollWheel", true));
            input.move = InputActionReference.Create(actions.FindAction("UI/Navigate", true));
            input.submit = InputActionReference.Create(actions.FindAction("UI/Submit", true));
            input.cancel = InputActionReference.Create(actions.FindAction("UI/Cancel", true));
            input.trackedDevicePosition = InputActionReference.Create(actions.FindAction("UI/TrackedDevicePosition", true));
            input.trackedDeviceOrientation = InputActionReference.Create(actions.FindAction("UI/TrackedDeviceOrientation", true));
            input.actionsAsset = actions;
            eventSystem.SetActive(true);
        }

        static void CreatePanelSettings()
        {
            Directory.CreateDirectory("Assets/GameOfLife/UI");
            PanelSettings settings;
            if (File.Exists(PanelSettingsPath))
                settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            else
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            }
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            // 运行时由工具栏按窗口高度放大（不小于 1 倍），这里只设基准像素尺寸。
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            settings.scale = 1;
            settings.sortingOrder = 10;
            settings.clearColor = false;
            settings.colorClearValue = Color.clear;
            EditorUtility.SetDirty(settings);
        }
    }
}
