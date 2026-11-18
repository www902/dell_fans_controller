// Tiny resgen replacement: reads a .resx and writes a .resources file.
// Skips entries that cannot be deserialized (old template junk such as Bitmap1/Icon1).
using System;
using System.Collections;
using System.Resources;
using System.Windows.Forms;

class MkRes
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: mkres <input.resx> <output.resources>");
            return 2;
        }
        string input = args[0];
        string output = args[1];
        int ok = 0, skipped = 0;
        using (ResXResourceReader reader = new ResXResourceReader(input))
        {
            reader.BasePath = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input));
            using (IResourceWriter writer = new ResourceWriter(output))
            {
                IDictionaryEnumerator e = reader.GetEnumerator();
                while (e.MoveNext())
                {
                    string key = (string)e.Key;
                    try
                    {
                        object value = e.Value;
                        if (value == null) { skipped++; continue; }
                        writer.AddResource(key, value);
                        ok++;
                        Console.WriteLine("  + " + key + " : " + value.GetType().FullName);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Console.WriteLine("  - skip " + key + " : " + ex.Message);
                    }
                }
            }
        }
        Console.WriteLine("wrote " + output + " (" + ok + " resources, " + skipped + " skipped)");
        return 0;
    }
}
