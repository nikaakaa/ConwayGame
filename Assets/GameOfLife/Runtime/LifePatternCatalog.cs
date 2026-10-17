using System.IO;
using UnityEngine;

namespace GameOfLife
{
    public static class LifePatternCatalog
    {
        public static void CopyTo(string directoryPath)
        {
            string sourceDirectory = Path.Combine(Application.streamingAssetsPath, "LifePatterns");
            foreach (string filePath in Directory.EnumerateFiles(sourceDirectory, "*.rle"))
                File.Copy(filePath, Path.Combine(directoryPath, Path.GetFileName(filePath)));
        }
    }
}
