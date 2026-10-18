using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace GameOfLife
{
    public sealed class LifeSession
    {
        readonly LifeSimulationSystem simulation;
        readonly EntityManager manager;
        readonly Entity board;
        int worldVersion;

        public LifePatternStore Patterns { get; }
        public LifeWorldStore Worlds { get; }
        public string PatternsDirectory { get; }
        public LifePattern CurrentSave { get; private set; }
        public bool HasWorld { get; private set; }
        public Task<string> SaveTask { get; private set; }
        public event Action<LifePattern> PatternStored;

        public LifeSession(LifeSimulationSystem simulation, EntityManager manager, Entity board)
        {
            this.simulation = simulation;
            this.manager = manager;
            this.board = board;
            PatternsDirectory = Path.Combine(Application.persistentDataPath, "Patterns");
            Patterns = new LifePatternStore(PatternsDirectory);
            Worlds = new LifeWorldStore(Path.Combine(Application.persistentDataPath, "Saves"));
            simulation.LoadPatterns(Patterns.Patterns);
        }

        public void NewWorld()
        {
            manager.GetBuffer<LifeCommand>(board).Add(new LifeCommand { Kind = LifeCommandKind.Clear });
            ResetViewport(int2.zero);
            CurrentSave = null;
            HasWorld = true;
            worldVersion++;
        }

        public void LoadWorld(LifePattern world, int2 anchor, LifePattern save)
        {
            LifeShape shape = simulation.Simulation.BuildShape(world.Tiles);
            DynamicBuffer<LifeCommand> commands = manager.GetBuffer<LifeCommand>(board);
            commands.EnsureCapacity(commands.Length + 2);
            commands.Add(new LifeCommand { Kind = LifeCommandKind.Clear });
            commands.Add(new LifeCommand { Kind = LifeCommandKind.PatternStroke,
                Shape = shape, Position = anchor, End = anchor });
            ResetViewport(anchor);
            CurrentSave = save;
            HasWorld = true;
            worldVersion++;
        }

        void ResetViewport(int2 center)
        {
            manager.SetComponentData(board, new LifeViewport
            {
                Origin = center - 64,
                VisibleCells = 128,
                FrameSeconds = 1.0 / 60
            });
        }

        public void Pause()
        {
            manager.GetBuffer<LifeCommand>(board).Add(new LifeCommand { Kind = LifeCommandKind.Pause });
        }

        public Task<string> SaveWorldAsync()
        {
            string name = CurrentSave != null ? CurrentSave.Name : "存档 " + DateTime.Now.ToString("MM-dd HH:mm");
            SaveTask = SaveSnapshotAsync(simulation.Simulation.CollectCellsAsync(false, default, default),
                name, CurrentSave, worldVersion);
            return SaveTask;
        }

        async Task<string> SaveSnapshotAsync(Task<int2[]> capture, string name, LifePattern overwrite, int version)
        {
            int2[] cells = await capture;
            if (cells.Length == 0)
                return null;
            string path = await Worlds.SaveAsync(name, cells, overwrite);
            Worlds.ReloadSaves();
            if (version == worldVersion)
                CurrentSave = Worlds.FindSave(path);
            return path;
        }

        public async Task<LifePattern> StoreCellsAsync(string name, bool selected, int2 minimum, int2 maximum)
        {
            int2[] cells = await simulation.Simulation.CollectCellsAsync(selected, minimum, maximum);
            if (cells.Length == 0)
                return null;
            LifePattern pattern = Patterns.Save(name, cells);
            simulation.LoadPatterns(Patterns.Patterns);
            PatternStored?.Invoke(pattern);
            return pattern;
        }

        public void DeleteSave(LifePattern save)
        {
            Worlds.DeleteSave(save);
            if (CurrentSave == save)
                CurrentSave = null;
        }

        public LifePattern RenameSave(LifePattern save, string name)
        {
            LifePattern renamed = Worlds.RenameSave(save, name);
            if (CurrentSave == save)
                CurrentSave = renamed;
            return renamed;
        }
    }
}
