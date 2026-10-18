using System;
using Unity.Mathematics;

namespace GameOfLife
{
    public struct LifeSimulationPolicy
    {
        const double TargetFrameSeconds = 1.0 / 60;
        const double MinimumBudgetSeconds = 0.00025;
        const double MaximumBudgetSeconds = 0.004;
        const double FrameToleranceSeconds = 0.0005;
        const double RecoveryPerFrameSeconds = 0.000125;
        const float MinimumSamplesPerSecond = 15;
        double budgetSeconds;

        public double BudgetSeconds => budgetSeconds;

        public LifeSimulationPolicy(double initialBudgetSeconds)
        {
            budgetSeconds = initialBudgetSeconds;
        }

        public double BeginFrame(double frameSeconds)
        {
            double remaining = TargetFrameSeconds - frameSeconds;
            if (remaining < -FrameToleranceSeconds)
                budgetSeconds = Math.Max(MinimumBudgetSeconds, budgetSeconds + remaining * 0.5);
            else if (remaining > FrameToleranceSeconds)
                budgetSeconds = Math.Min(MaximumBudgetSeconds,
                    budgetSeconds + Math.Min(RecoveryPerFrameSeconds, remaining * 0.05));
            return budgetSeconds;
        }

        public static int StepExponent(int visibleCells, float generationsPerSecond)
        {
            int scaleExponent = math.max(0, math.tzcnt((uint)visibleCells) - 7);
            float speedStep = generationsPerSecond / MinimumSamplesPerSecond;
            int exponent = 0;
            while (exponent < scaleExponent && (1L << (exponent + 1)) <= speedStep)
                exponent++;
            return exponent;
        }

        public static double AccumulatorCapacity(long step, float generationsPerSecond)
        {
            return Math.Max(step * 2, generationsPerSecond / 30.0);
        }
    }
}
