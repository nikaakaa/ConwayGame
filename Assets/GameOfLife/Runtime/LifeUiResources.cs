using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameOfLife
{
    public sealed class LifeUiResources : IDisposable
    {
        public Font Font { get; }
        public Font MonoFont { get; }
        readonly PanelSettings panelSettings;
        int scaledHeight;

        public LifeUiResources(UIDocument document)
        {
            Font = UnityEngine.Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
            MonoFont = UnityEngine.Font.CreateDynamicFontFromOSFont("Consolas", 14);
            panelSettings = UnityEngine.Object.Instantiate(document.panelSettings);
            document.panelSettings = panelSettings;
            UpdateScale();
        }

        public void UpdateScale()
        {
            if (Screen.height == scaledHeight)
                return;
            scaledHeight = Screen.height;
            panelSettings.scale = math.max(1f, Screen.height / 1080f);
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(Font);
            UnityEngine.Object.Destroy(MonoFont);
            UnityEngine.Object.Destroy(panelSettings);
        }
    }
}
