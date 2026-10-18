using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameOfLife
{
    public static class LifeSceneFlow
    {
        public const string MainMenu = "MainMenu";
        public const string Game = "GameOfLife";

        public static AsyncOperation OpenGame() => SceneManager.LoadSceneAsync(Game, LoadSceneMode.Single);

        public static AsyncOperation OpenMenu(LifeSession session)
        {
            session.Pause();
            return SceneManager.LoadSceneAsync(MainMenu, LoadSceneMode.Single);
        }

        public static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
