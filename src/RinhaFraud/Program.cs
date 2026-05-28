namespace RinhaFraud;

using System;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var command = args.Length == 0 ? "serve" : args[0];
        switch (command)
        {
            case "build-index":
                if (args.Length != 2)
                {
                    Console.Error.WriteLine("usage: rinha-fraud build-index <output.idx> < references.json");
                    return 2;
                }

                BinaryIndexBuilder.Build(args[1], Console.OpenStandardInput());
                return 0;

            case "serve":
                HttpServer.Serve();
                return 0;

            case "eval":
                if (args.Length != 2)
                {
                    Console.Error.WriteLine("usage: rinha-fraud eval <test-data.json>");
                    return 2;
                }

                EvalCommand.Run(args[1]);
                return 0;

            case "index-info":
                if (args.Length != 2)
                {
                    Console.Error.WriteLine("usage: rinha-fraud index-info <references.idx>");
                    return 2;
                }

                using (var index = BinaryIndex.Open(args[1]))
                {
                    Console.Write(index.BuildInfo);
                }

                return 0;

            case "self-test":
                SelfTest.Run();
                return 0;

            case "-h":
            case "--help":
                Console.WriteLine("usage:");
                Console.WriteLine("  rinha-fraud serve");
                Console.WriteLine("  rinha-fraud build-index <output.idx> < references.json");
                Console.WriteLine("  rinha-fraud eval <test-data.json>");
                Console.WriteLine("  rinha-fraud index-info <references.idx>");
                Console.WriteLine("  rinha-fraud self-test");
                return 0;

            default:
                Console.Error.WriteLine($"unknown command: {command}");
                return 2;
        }
    }
}
