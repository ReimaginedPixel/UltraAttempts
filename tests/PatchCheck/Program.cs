using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

// Applies each of UltraAttempts' Harmony hooks to the real Assembly-CSharp. Outside Unity the last step
// (compiling the patched method) can fail on Unity's engine calls ("ECall methods must be packaged into
// a system module"): by then Harmony has found the method and accepted the hook's parameters, so that
// counts as a pass. Any other failure is a real one.
internal static class Program
{
    // Every hook in Patches: cheats, scene loads, restarts, results, rooms off, doors opening and
    // closing, explosions, beams (hitscan and lasting), breakables, the camera, Escape, continuing from the
    // results screen; the game's sounds (Play, PlayOneShot, PlayDelayed, and its virtual audio picking up
    // the ones that play by themselves); and what it makes (Unity's four non-generic Instantiates, for its
    // effects and projectiles).
    private const int Expected = 22;

    private static string game;
    private static string plugin;

    private static int Main()
    {
        game = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "GameDir").Value;
        plugin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bin", "Release", "netstandard2.1", "UltraAttempts.dll"));
        string[] dirs = { Path.Combine(game, "ULTRAKILL_Data", "Managed"), Path.Combine(game, "BepInEx", "core") };
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            if (name == "0Harmony")
            {
                return typeof(Harmony).Assembly;  // (this harness's Harmony stands in for BepInEx's)
            }
            foreach (string dir in dirs)
            {
                string path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path) && !name.StartsWith("System") && name != "mscorlib" && name != "netstandard")
                {
                    return Assembly.LoadFrom(path);
                }
            }
            return null;
        };
        return Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        Console.WriteLine("plugin: " + plugin);
        Assembly assembly = Assembly.LoadFrom(plugin);
        Type patches = assembly.GetType("UltraAttempts.Patches", throwOnError: true);
        var harmony = new Harmony("ultraattempts.patchcheck");
        int ok = 0, failed = 0;
        // (and the classes of hooks inside it, patched on their own)
        IEnumerable<MethodInfo> hooks = new[] { patches }.Concat(patches.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
        foreach (MethodInfo hook in hooks)
        {
            var target = hook.GetCustomAttributes<HarmonyPatch>().Select(a => a.info).FirstOrDefault(i => i.declaringType != null);
            if (target == null)
            {
                continue;
            }
            string label = $"{target.declaringType.Name}.{target.methodName}" + (target.argumentTypes != null ? $"({string.Join(", ", target.argumentTypes.Select(t => t.Name))})" : "");
            MethodBase original = AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes);
            if (original == null)
            {
                Console.WriteLine($"  FAIL  {label}: not found");
                failed++;
                continue;
            }
            bool prefix = hook.GetCustomAttributes<HarmonyPrefix>().Any();
            string[] originalParams = original.GetParameters().Select(p => p.Name).ToArray();
            string bad = hook.GetParameters().Select(p => p.Name).FirstOrDefault(n => !n.StartsWith("__") && !originalParams.Contains(n));
            if (bad != null)
            {
                Console.WriteLine($"  FAIL  {label}: no parameter called '{bad}'");
                failed++;
                continue;
            }
            try
            {
                harmony.Patch(original, prefix: prefix ? new HarmonyMethod(hook) : null, postfix: prefix ? null : new HarmonyMethod(hook));
                Console.WriteLine($"  ok    {label} ({(prefix ? "prefix" : "postfix")}, applied)");
                ok++;
            }
            catch (Exception e) when (e.GetBaseException().Message.Contains("ECall"))
            {
                Console.WriteLine($"  ok    {label} ({(prefix ? "prefix" : "postfix")}, accepted; only Unity can compile it)");
                ok++;
            }
            catch (Exception e)
            {
                Console.WriteLine($"  FAIL  {label}: {e.GetBaseException().Message}");
                failed++;
            }
        }
        Console.WriteLine(failed == 0 && ok == Expected ? $"all {Expected} hooks found and accepted" : $"{failed} failed, {ok} ok (expected {Expected})");
        int fields = PrivateFields();
        return failed == 0 && ok == Expected && fields == 0 ? 0 : 1;
    }

    // The game's private fields UltraAttempts reads by name (AccessTools.FieldRefAccess): each must be there,
    // of the type it's read as.
    private static readonly (string type, string field, string fieldType)[] Fields =
    {
        ("Explosion", "originalMaterial", "Material"),
        ("Explosion", "materialColor", "Color"),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PrivateFields()
    {
        int failed = 0;
        foreach (var (type, field, fieldType) in Fields)
        {
            Type t = Type.GetType(type + ", Assembly-CSharp");
            FieldInfo f = t != null ? AccessTools.Field(t, field) : null;
            bool ok = f != null && f.FieldType.Name == fieldType;
            Console.WriteLine(ok ? $"  ok    {type}.{field} ({fieldType}, read by name)" : $"  FAIL  {type}.{field}: {(t == null ? "no such type" : f == null ? "no such field" : "it's a " + f.FieldType.Name)}");
            failed += ok ? 0 : 1;
        }
        Console.WriteLine(failed == 0 ? $"all {Fields.Length} fields read by name are there" : $"{failed} of {Fields.Length} fields read by name are missing");
        return failed;
    }
}
