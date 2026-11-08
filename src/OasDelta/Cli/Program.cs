namespace OasDelta.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false), 1 << 16) { AutoFlush = false, NewLine = "\n" };
        var stderr = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // test support for tools/check_golden.sh; inert unless the variable is set
        if (Environment.GetEnvironmentVariable(GoldenBatch.Variable) is { Length: > 0 } caseList)
        {
            try { return GoldenBatch.Run(caseList, stdout); }
            finally { stdout.Flush(); }
        }

        try { return Cli.Run(args, stdout, stderr); }
        catch (Exception e)
        {
            stdout.Flush();
            stderr.WriteLine("Error: " + e.Message);
            return 100;
        }
        finally
        {
            stdout.Flush();
            if (Environment.GetEnvironmentVariable("OASDELTA_TIMING") is { Length: > 0 })
                Console.Error.WriteLine($"[timing] total in Main: {sw.Elapsed.TotalMilliseconds:F1} ms (process uptime {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms)");
        }
    }
}
