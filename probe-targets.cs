using System;
using System.IO;
using System.Linq;
using System.Reflection;

class Probe
{
    static int Main(string[] args)
    {
        string root = args[0];
        string[] dirs = {
            Path.Combine(root, "Mortal_Data/Managed"),
            Path.Combine(root, "BepInEx/core"),
            Path.Combine(root, "LOMDailyAutomation-src/bin")
        };
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string n = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in dirs)
            {
                string p = Path.Combine(d, n);
                if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { } }
            }
            return null;
        };
        var asm = Assembly.LoadFrom(Path.Combine(root, "LOMDailyAutomation-src/bin/LOMDailyAutomation.dll"));
        var t = asm.GetType("LOMDailyAutomation.DailyAutomationPlugin", true);
        var menuPatch = t.GetNestedType("MenuOptionPatch", BindingFlags.NonPublic);
        var tm = menuPatch.GetMethod("TargetMethod", BindingFlags.NonPublic | BindingFlags.Static);
        var target = (MethodBase)tm.Invoke(null, null);
        Console.WriteLine("menu target=" + (target == null ? "(null)" : target.DeclaringType.FullName + "." + target.Name));
        var storyPatch = t.GetNestedType("StoryTextPatch", BindingFlags.NonPublic);
        var stm = storyPatch.GetMethod("TargetMethod", BindingFlags.NonPublic | BindingFlags.Static);
        var st = (MethodBase)stm.Invoke(null, null);
        Console.WriteLine("story target=" + (st == null ? "(null)" : st.DeclaringType.FullName + "." + st.Name));
        foreach (var name in new[] { "MartialPanelOpenPatch", "UpgradePanelOpenPatch" })
        {
            var nt = t.GetNestedType(name, BindingFlags.NonPublic);
            Console.WriteLine(name + " present=" + (nt != null));
            if (nt != null)
            {
                foreach (var a in nt.GetCustomAttributesData())
                {
                    Console.Write("  attr: " + a.AttributeType.FullName);
                    foreach (var arg in a.ConstructorArguments) Console.Write(" [" + arg.Value + "]");
                    Console.WriteLine();
                }
            }
        }
        Console.WriteLine("DriveMartialPanel=" + (t.GetMethod("DriveMartialPanel", BindingFlags.NonPublic | BindingFlags.Instance) != null));
        Console.WriteLine("DriveUpgradePanel=" + (t.GetMethod("DriveUpgradePanel", BindingFlags.NonPublic | BindingFlags.Instance) != null));
        Console.WriteLine("AreAllPanelsExhausted=" + (t.GetMethod("AreAllPanelsExhausted", BindingFlags.NonPublic | BindingFlags.Instance) != null));
        Console.WriteLine("DrawDuelControls=" + (t.GetMethod("DrawDuelControls", BindingFlags.NonPublic | BindingFlags.Instance) != null));
        Console.WriteLine("ResolveDuel=" + (t.GetMethod("ResolveDuel", BindingFlags.NonPublic | BindingFlags.Instance) != null));
        var pluginAttr = t.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().FullName == "BepInEx.BepInPlugin");
        Console.WriteLine("plugin=" + pluginAttr);
        return 0;
    }
}
