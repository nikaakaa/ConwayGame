using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace GameOfLife
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class LifeSelectionGraphic : MaskableGraphic
    {
        const int DashLength = 6;
        static readonly Color32 White = new Color32(255, 255, 255, 255);
        static readonly Color32 Dim = new Color32(110, 110, 110, 255);

        int2 origin;
        int visibleCells;
        int2 minimum;
        int2 maximum;

        public void SetFrame(int2 origin, int visibleCells, int2 min, int2 max)
        {
            this.origin = origin;
            this.visibleCells = visibleCells;
            minimum = min;
            maximum = max;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper helper)
        {
            helper.Clear();
            var raster = new LifeRasterizer { TextureSize = LifeView.TextureSize, VisibleCells = visibleCells };
            long lowX = (long)minimum.x - origin.x;
            long lowY = (long)minimum.y - origin.y;
            long highX = (long)maximum.x + 1 - origin.x;
            long highY = (long)maximum.y + 1 - origin.y;
            int2 begin = new int2(raster.PixelBegin(lowX), raster.PixelBegin(lowY));
            int2 end = new int2(raster.PixelEnd(highX), raster.PixelEnd(highY));
            if (begin.x >= end.x || begin.y >= end.y)
                return;
            Rect bounds = rectTransform.rect;
            Vector2 pixelSize = bounds.size / LifeView.TextureSize;
            if (lowY >= 0 && lowY <= visibleCells)
                DrawEdge(helper, bounds.min, pixelSize, begin.x, end.x, begin.y, true);
            if (highY >= 0 && highY <= visibleCells)
                DrawEdge(helper, bounds.min, pixelSize, begin.x, end.x, end.y - 1, true);
            if (lowX >= 0 && lowX <= visibleCells)
                DrawEdge(helper, bounds.min, pixelSize, begin.y, end.y, begin.x, false);
            if (highX >= 0 && highX <= visibleCells)
                DrawEdge(helper, bounds.min, pixelSize, begin.y, end.y, end.x - 1, false);
        }

        static void DrawEdge(VertexHelper helper, Vector2 offset, Vector2 pixelSize,
            int begin, int end, int fixedPixel, bool horizontal)
        {
            for (int cursor = begin; cursor < end;)
            {
                int dash = cursor / DashLength;
                int next = math.min(end, (dash + 1) * DashLength);
                Color32 shade = (dash & 1) == 0 ? White : Dim;
                int minX = horizontal ? cursor : fixedPixel;
                int minY = horizontal ? fixedPixel : cursor;
                int maxX = horizontal ? next : fixedPixel + 1;
                int maxY = horizontal ? fixedPixel + 1 : next;
                Vector2 low = offset + new Vector2(minX * pixelSize.x, minY * pixelSize.y);
                Vector2 high = offset + new Vector2(maxX * pixelSize.x, maxY * pixelSize.y);
                int first = helper.currentVertCount;
                helper.AddVert(new Vector3(low.x, low.y), shade, Vector2.zero);
                helper.AddVert(new Vector3(low.x, high.y), shade, Vector2.zero);
                helper.AddVert(new Vector3(high.x, high.y), shade, Vector2.zero);
                helper.AddVert(new Vector3(high.x, low.y), shade, Vector2.zero);
                helper.AddTriangle(first, first + 1, first + 2);
                helper.AddTriangle(first + 2, first + 3, first);
                cursor = next;
            }
        }
    }
}
