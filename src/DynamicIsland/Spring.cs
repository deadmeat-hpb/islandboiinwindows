namespace DynamicIsland;

/// <summary>Damped spring. Low damping relative to stiffness = more bounce.</summary>
struct Spring(float value, float stiffness, float damping)
{
    public float Value = value, Target = value, Velocity;
    public readonly float Stiffness = stiffness, Damping = damping;

    public readonly bool Settled => MathF.Abs(Target - Value) < 0.05f && MathF.Abs(Velocity) < 0.05f;

    /// <summary>Jump straight to the target (Animations: Off).</summary>
    public void Snap() { Value = Target; Velocity = 0; }

    public void Step(float dt)
    {
        const int substeps = 4;
        float h = dt / substeps;
        for (int i = 0; i < substeps; i++)
        {
            float accel = Stiffness * (Target - Value) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
        }
        if (Settled) { Value = Target; Velocity = 0; }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    public static void SelfCheck()
    {
        var s = new Spring(0, 300, 20) { Target = 100 };
        for (int i = 0; i < 180; i++) s.Step(1 / 60f); // 3 seconds
        System.Diagnostics.Debug.Assert(s.Settled && s.Value == 100, "spring should settle on its target");
    }
}
