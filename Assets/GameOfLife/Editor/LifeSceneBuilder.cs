using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace GameOfLife.Editor
{
    public static class LifeSceneBuilder
    {
        const string ScenePath = "Assets/GameOfLife/Scenes/GameOfLife.unity";
        const string PanelSettingsPath = "Assets/GameOfLife/UI/LifeEditorPanel.asset";
        const string ThemePath = "Assets/GameOfLife/UI/LifeEditorTheme.tss";

        [MenuItem("Tools/Game of Life/Create Demo Scene")]
        public static void CreateDemoScene()
        {
            Directory.CreateDirectory("Assets/GameOfLife/Scenes");
            var previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(UniversalAdditionalCameraData));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0, 0, -10);
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;

            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            eventSystem.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();

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
            editorObject.GetComponent<UIDocument>().panelSettings = CreatePanelSettings();
            LifeEditorToolbar toolbar = editorObject.AddComponent<LifeEditorToolbar>();
            toolbar.Controller = controller;
            toolbar.View = view;

            EditorSceneManager.SaveScene(scene, ScenePath);
            var buildScenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
            foreach (EditorBuildSettingsScene item in EditorBuildSettings.scenes)
                if (item.path != ScenePath)
                    buildScenes.Add(item);
            EditorBuildSettings.scenes = buildScenes.ToArray();
            AssetDatabase.SaveAssets();
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
            EditorSceneManager.CloseScene(scene, true);
            Debug.Log("Game of Life 场景已保存：" + ScenePath);
        }

        static PanelSettings CreatePanelSettings()
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
            return settings;
        }
    }
}
