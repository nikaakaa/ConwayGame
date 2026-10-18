using System.IO;
using System.Threading.Tasks;
using GameOfLife;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public static class RestoreLifeInteraction
{
    public static async Task<object> Main(bool resumeRunning)
    {
        var metadata = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(
            "Tools/Benchmarks/Work/interaction-restore.json", System.Text.Encoding.UTF8));
        Application.runInBackground = true;
        var controller = UnityEngine.Object.FindFirstObjectByType<LifeController>();
        var save = controller.Worlds.FindSave(Path.GetFullPath((string)metadata["savePath"]));
        var loaded = await controller.Worlds.LoadAsync(save);
        controller.LoadWorld(loaded.World, loaded.Anchor, save);
        RectTransform rect = controller.View.BoardImage.rectTransform;
        Vector2 center = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
        int size = (int)metadata["size"];
        while (controller.View.VisibleCells != size)
            controller.View.Zoom(center, null, controller.View.VisibleCells > size);
        controller.View.Pan(new int2((int)metadata["originX"], (int)metadata["originY"]) - controller.View.Origin);
        controller.SetSpeed((float)metadata["speed"]);
        var manager = World.DefaultGameObjectInjectionWorld.EntityManager;
        EntityQuery query = manager.CreateEntityQuery(ComponentType.ReadOnly<LifeState>());
        Entity board = query.GetSingletonEntity();
        query.Dispose();
        while (manager.GetBuffer<LifeCommand>(board).Length != 0)
            await Task.Delay(1);
        var toolbar = UnityEngine.Object.FindFirstObjectByType<LifeEditorToolbar>();
        typeof(LifeEditorToolbar).GetMethod("HideStart",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(toolbar, null);
        if (resumeRunning)
        {
            if ((bool)metadata["wasRunning"]) controller.ToggleRunning();
            while (manager.GetBuffer<LifeCommand>(board).Length != 0)
                await Task.Delay(1);
            Application.runInBackground = (bool)metadata["oldBackground"];
            UnityEditorInternal.ProfilerDriver.enabled = (bool)metadata["oldHistory"];
        }
        return new { population = loaded.World.Population, anchorX = loaded.Anchor.x, anchorY = loaded.Anchor.y,
            originX = controller.View.Origin.x, originY = controller.View.Origin.y, controller.View.VisibleCells,
            resumed = resumeRunning, sourceGeneration = (long)metadata["sourceGeneration"], generationRestartsAtZero = true };
    }
}
