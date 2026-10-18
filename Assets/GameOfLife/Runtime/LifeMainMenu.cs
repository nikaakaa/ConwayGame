using System.Collections;
using System.Threading.Tasks;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameOfLife
{
    public sealed class LifeMainMenu : MonoBehaviour
    {
        LifeSession session;
        LifeUiResources resources;
        LifeStartScreen screen;
        LifePatternLibrary saves;
        LifePatternLibrary maps;
        VisualElement root;

        void Start()
        {
            session = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LifeSimulationSystem>().Session;
            resources = new LifeUiResources(GetComponent<UIDocument>());
            Build();
        }

        void Build()
        {
            screen?.Dispose();
            saves?.Dispose();
            maps?.Dispose();
            root = GetComponent<UIDocument>().rootVisualElement;
            root.Clear();
            root.pickingMode = PickingMode.Ignore;
            root.style.unityFontDefinition = FontDefinition.FromFont(resources.Font);
            saves = new LifePatternLibrary(root, resources.MonoFont, () => session.Worlds.Saves, session.Worlds.IsSave)
            {
                RenameRequested = session.RenameSave,
                DeleteRequested = session.DeleteSave,
                PreviewImage = session.Worlds.Preview,
                AnimatePreviews = false,
                Title = "存档",
                PickTip = "载入  ↵"
            };
            maps = new LifePatternLibrary(root, resources.MonoFont, () => session.Worlds.Maps, _ => false)
            {
                PreviewImage = session.Worlds.Preview,
                AnimatePreviews = false,
                Title = "地图",
                PickTip = "载入  ↵"
            };
            saves.Picked += EnterWorld;
            maps.Picked += EnterWorld;
            screen = new LifeStartScreen(root, resources.MonoFont);
            screen.NewWorldRequested += () =>
            {
                session.NewWorld();
                LifeSceneFlow.OpenGame();
            };
            screen.ContinueRequested += ContinueWorld;
            screen.SavesRequested += () => saves.Open(session.CurrentSave);
            screen.MapsRequested += () =>
            {
                session.Worlds.ReloadMaps();
                maps.Open(null);
            };
            screen.QuitRequested += LifeSceneFlow.Quit;
            session.Worlds.ReloadSaves();
            screen.Show(session.HasWorld || session.Worlds.Saves.Count > 0);
        }

        void ContinueWorld()
        {
            if (session.HasWorld)
                LifeSceneFlow.OpenGame();
            else
                EnterWorld(session.Worlds.Saves[0]);
        }

        void EnterWorld(LifePattern entry) => StartCoroutine(LoadWorld(entry));

        IEnumerator LoadWorld(LifePattern entry)
        {
            screen.ShowLoading();
            Task<(LifePattern World, int2 Anchor)> load = session.Worlds.LoadAsync(entry);
            while (!load.IsCompleted)
                yield return null;
            if (load.IsFaulted)
            {
                Debug.LogException(load.Exception);
                screen.HideLoading();
                yield break;
            }
            yield return null;
            yield return null;
            session.LoadWorld(load.Result.World, load.Result.Anchor, session.Worlds.IsSave(entry) ? entry : null);
            LifeSceneFlow.OpenGame();
        }

        void Update()
        {
            bool overlayOpen = saves.IsOpen || maps.IsOpen;
            saves.Tick();
            maps.Tick();
            screen.Tick(!overlayOpen, session.HasWorld);
        }

        void LateUpdate()
        {
            resources.UpdateScale();
            if (root.panel == null)
                Build();
        }

        void OnDestroy()
        {
            screen.Dispose();
            saves.Dispose();
            maps.Dispose();
            session.Worlds.ReleasePreviews();
            resources.Dispose();
        }
    }
}
