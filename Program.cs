using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GLtf = Novorender.GLtf;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImageMagick;
using System.Numerics;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PDFReader
{
    public class CoordnateSettings
    {
        public double Density { get; set; }
        public double CornerX { get; set; }
        public double CornerY { get; set; }
        public double LengthX { get; set; }
        public double LengthY { get; set; }
    }


    public class VertexBufferBuilderPT : GLtf.VertexWriter
    {
        uint _count;
        Vector3 _minPosition = new Vector3(float.MaxValue);
        Vector3 _maxPosition = new Vector3(float.MinValue);

        readonly GLtf.Builder _builder;
        readonly long _byteOffset;
        readonly long _buffer;
        readonly static int _floatByteSize = Marshal.SizeOf<float>();
        readonly static int _numFloats = (3 + 2);
        readonly static int _byteStride = _floatByteSize * _numFloats;

        internal VertexBufferBuilderPT(GLtf.Builder builder, long buffer = 0) :
            base(builder.Buffer, GLtf.VertexAttribute.POSITION, GLtf.VertexAttribute.TEXCOORD_0_FLOAT)
        {
            _builder = builder;
            _byteOffset = builder.PadBuffer(4);
            _buffer = buffer;
        }

        public uint Count => _count;

        unsafe public uint Add(Vector3 pos, Vector2 uv)
        {
            var current = ValuesBuffer;
            current->Position = pos;
            current->TexCoord_0 = uv;
            base.WriteValues();
            _minPosition = Vector3.Min(_minPosition, pos);
            _maxPosition = Vector3.Max(_maxPosition, pos);
            return _count++;
        }

        public (int positionAccessor, int uvAccessor) Finish()
        {
            var minPos = new System.DoubleNumerics.Vector3(_minPosition.X, _minPosition.Y, _minPosition.Z);
            var maxPos = new System.DoubleNumerics.Vector3(_maxPosition.X, _maxPosition.Y, _maxPosition.Z);
            var bufferView = _builder.AddBufferView(_buffer, _count * _byteStride, _byteOffset, _byteStride, GLtf.BufferTarget.ARRAY_BUFFER);
            var positionAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC3", bufferView, 0, minPos, maxPos);
            var uvAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC2", bufferView, 12);
            return (positionAccessor, uvAccessor);
        }
    }


    class Program
    {
        static int Main(string[] args)
        {
            FileInfo file = new FileInfo(args[0]);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Console.Write("Loading.");
            //var settings = new CoordnateSettings();

            // using (StreamReader r = new StreamReader(args[1]))
            // {
            //     string json = r.ReadToEnd();
            //     settings = JsonConvert.DeserializeObject<CoordnateSettings>(json);
            // }

            var density = 500.0;
            if (args.Count() > 1)
            {
                density = Convert.ToDouble(args[1]);
            }

            PdfToImageConverter pdf = new PdfToImageConverter(file);
            pdf.ConvertFileToImages(file, "C:\\tmp\\", density);
            timer.Stop();
            Console.WriteLine($"Write complete in {timer.Elapsed}");
            return 0;
        }
    }

    public class PdfToImageConverter
    {
        DirectoryInfo tmpDir;
        string tmpFile;
        public PdfToImageConverter(FileInfo inputFile)
        {
            tmpDir = new DirectoryInfo(inputFile.Directory.ToString() + "\\Tmp");
            //MagickNET.SetGhostscriptDirectory(filePath + "Lib");
            Directory.CreateDirectory(tmpDir.ToString());
            MagickNET.SetTempDirectory(tmpDir.ToString());
            tmpFile = tmpDir.ToString() + "\\tmp.png";
        }
        public void ConvertFileToImages(FileInfo file, string destinationPath, double initialDensity)
        {
            var fileName = Path.GetFileNameWithoutExtension(file.Name);

            double currentDensity = initialDensity;
            const int tileSize = 256;
            var pageTresholdReached = new List<bool>();
            string output = file.Directory.ToString() + "\\" + fileName + "\\";
            Directory.CreateDirectory(output);
            int lodDepth = 0;
            int filesWritten = 0;
            MagickReadSettings magickReadSettings = new MagickReadSettings();
            var lods = new List<MagickImageCollection>();
            while (currentDensity > 10)
            {
                Console.WriteLine($"LOD {lodDepth}...");

                bool tresholdReached = true;
                magickReadSettings.Density = new Density(currentDensity, currentDensity);
                lods.Add(new MagickImageCollection());
                var collection = lods[lods.Count - 1];
                collection.Read(file.ToString(), magickReadSettings);
                int pageIdx = 0;
                foreach (MagickImage magickImage in collection)
                {
                    if (pageIdx == pageTresholdReached.Count)
                    {
                        pageTresholdReached.Add(false);
                        Directory.CreateDirectory($"{output}page_{pageIdx}");
                    }
                    if (pageTresholdReached[pageIdx])
                    {
                        ++pageIdx;
                        continue;
                    }
                    if (magickImage.Height <= tileSize && magickImage.Width <= tileSize)
                    {
                        pageTresholdReached[pageIdx] = true;
                    }
                    tresholdReached = false;
                    currentDensity = currentDensity / 2;
                }
                if (tresholdReached)
                {
                    break;
                }
            }

            foreach (var magickImageCollection in lods)
            {
                int pageIdx = 0;
                foreach (MagickImage magickImage in magickImageCollection)
                {
                    var pixels = magickImage.GetPixels();
                    // string imageFilePath = string.Concat(destinationPath, "file-", pageIdx, ".png");
                    // magickImage.Write(imageFilePath);
                    int heightTileSize = 0;
                    int widthTileSize = 0;
                    int noIteration = 0;
                    if (magickImage.Width > magickImage.Height)
                    {
                        var d = (double)magickImage.Width / (double)tileSize;
                        noIteration = (int)Math.Ceiling(d);
                        widthTileSize = tileSize;
                        heightTileSize = (int)Math.Ceiling((double)magickImage.Height / (double)noIteration);
                    }
                    else
                    {
                        var d = (double)magickImage.Height / (double)tileSize;
                        noIteration = (int)Math.Ceiling(d);
                        heightTileSize = tileSize;
                        widthTileSize = (int)Math.Ceiling((double)magickImage.Width / (double)noIteration);
                    }
                    var aspect = magickImage.Width / magickImage.Height;
                    var wsDx = aspect / noIteration;
                    var wsDy = 1 / noIteration;
                    for (var i = 0; i < noIteration; ++i)
                    {
                        int x = i * widthTileSize;
                        int endWidth = x + widthTileSize;
                        int dx = endWidth > magickImage.Width ? magickImage.Width - x : widthTileSize;
                        for (var j = 0; j < noIteration; ++j)
                        {
                            using (var builder = new GLtf.Builder())
                            {
                                var id = new string(Enumerable.Range(0, lods.Count - lodDepth).Select(k => (char)('0' + ((i & (1 << k)) != 0 ? 1 : 0) | ((j & (1 << k)) != 0 ? 2 : 0))).Reverse().ToArray());
                                var glbFilename = $"{output}page_{pageIdx}/_{id}";
                                Console.WriteLine(glbFilename);

                                int y = j * heightTileSize;
                                int endHeight = y + heightTileSize;
                                int dy = endHeight > magickImage.Height ? magickImage.Height - y : heightTileSize;
                                var pixelArea = pixels.GetArea(x, y, dx, dy);
                                var tiledImage = new MagickImage();
                                var settings = new PixelReadSettings(dx, dy, StorageType.Quantum, PixelMapping.RGBA);
                                tiledImage.ReadPixels(pixelArea.AsSpan(), settings);
                                tiledImage.Format = MagickFormat.Png;
                                tiledImage.Write(tmpFile);

                                var imgBlob = System.IO.File.ReadAllBytes(tmpFile);
                                var (bufferBegin, bufferEnd) = builder.Buffer.AddRange(imgBlob);
                                var imageBufferView = builder.AddBufferView(0, imgBlob.Length, bufferBegin);
                                var imgIdx = builder.AddImage("image / png", imageBufferView);
                                var baseTexture = builder.AddTexture(imgIdx, null);

                                var material = builder.AddUnlitMaterial(rgba: new System.DoubleNumerics.Vector4(1, 1, 1, 1), alphaMode: GLtf.AlphaMode.BLEND, baseColorTexture: builder.CreateTextureInfo(baseTexture), doubleSided: true);

                                using (var vertexBuffer = new VertexBufferBuilderPT(builder))
                                {
                                    var localCornerX = i * wsDx;
                                    var localCornerY = (j + 1) * wsDy;
                                    Vector3 a = new Vector3((float)localCornerX, (float)localCornerY, 0);
                                    Vector3 b = new Vector3((float)localCornerX + (float)wsDx, (float)localCornerY, 0);
                                    Vector3 c = new Vector3((float)localCornerX + (float)wsDx, (float)localCornerY + (float)wsDy, 0);
                                    Vector3 d = new Vector3((float)localCornerX, (float)localCornerY + (float)wsDy, 0);
                                    vertexBuffer.Add(a, new Vector2(0, 1));
                                    vertexBuffer.Add(b, new Vector2(1, 1));
                                    vertexBuffer.Add(c, new Vector2(1, 0));

                                    vertexBuffer.Add(a, new Vector2(0, 1));
                                    vertexBuffer.Add(c, new Vector2(1, 0));
                                    vertexBuffer.Add(d, new Vector2(0, 0));

                                    var (positionAccessor, uvAccessor) = vertexBuffer.Finish();
                                    var attributes = new[]
                                    {
                                            builder.CreateAttribute("POSITION", positionAccessor),
                                            // builder.CreateAttribute("NORMAL", normalAccessor),
                                            builder.CreateAttribute("TEXCOORD_0", uvAccessor),
                                        };
                                    var primitive = builder.CreatePrimitive(attributes, mode: GLtf.DrawMode.TRIANGLES, material: material);
                                    var meshIdx = builder.AddMesh(new[] { primitive });
                                    var nodeIdx = builder.AddNode(name: $"{pageIdx.ToString()}_{i.ToString()}_{j.ToString()}", mesh: meshIdx);
                                    builder.AddScene(nodes: new[] { nodeIdx });
                                    builder.Write(glbFilename, true);
                                    ++filesWritten;
                                }
                            }
                        }

                        if (magickImage.Height <= tileSize && magickImage.Width <= tileSize)
                        {
                            pageTresholdReached[pageIdx] = true;
                        }
                    }
                    ++pageIdx;
                }
                ++lodDepth;
            }
            string[] tmpFiles = Directory.GetFiles(tmpDir.ToString());
            foreach (var tmpFile in tmpFiles)
            {
                File.Delete(tmpFile);
            }
            Directory.Delete(tmpDir.ToString());
        }
    }
}

