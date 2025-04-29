using System;
using CommandLineParser.Arguments;
using System.IO;
using static System.Console;

namespace NovoRender.PDFReader
{
    class Arguments
    {
        public static Arguments Parse(string[] args)
        {
            var parser = new CommandLineParser.CommandLineParser();
            parser.ShowUsageOnEmptyCommandline = true;
            parser.ShowUsageHeader = "OctreeCreator.exe";
            var arguments = new Arguments();
            parser.ExtractArgumentAttributes(arguments);
            try
            {
                parser.ParseCommandLine(args);
                if (parser.ParsingSucceeded)
                {
                    return arguments;
                }
            }
            catch (Exception e)
            {
                WriteLine(e.Message);
                parser.ShowUsage();
            }
            return null;
        }

        [FileArgument('i', "input", Optional = false, Example = "Document.pdf", FileMustExist = true, Description = "Input PDF-file")]
        public FileInfo File { get; set; }

        [DirectoryArgument('o', "output", Optional = false, Example = "outdir", DirectoryMustExist = false, Description = "Output directory")]
        public DirectoryInfo OutputFolder { get; set; }

        [ValueArgument(typeof(int), "tile-size", Optional = true, Example = "256", DefaultValue = 256, Description = "Tile size")]
        public int TileSize { get; set; }

        [ValueArgument(typeof(string), 'e', "epsg", Optional = true, Example = "EPSG:4326", DefaultValue = "EPSG:4326", Description = "EPSG code")]
        public string Epsg { get; set; }

        [ValueArgument(typeof(double), "density", Optional = true, Example = "500", DefaultValue = 500, Description = "PDF density")]
        public double Density { get; set; }
    }
}
