namespace AbpPlugin;

/// <summary>
/// Says a plugin may use types and members a server is older than, because it checks for them and falls back
/// when they're missing: it calls them only from methods kept apart with <c>[MethodImpl(MethodImplOptions.NoInlining)]</c>
/// and catches <see cref="MissingMethodException"/> and <see cref="TypeLoadException"/> around those calls. Without it,
/// a server refuses to install a plugin that uses anything it hasn't got; with it, the server installs it and logs what
/// it lacks.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ToleratesOlderHostsAttribute : Attribute;
