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

namespace NovoRender.PDFReader
{
    public class VertexBufferBuilderPT : GLtf.VertexWriter
    {
        uint _count;
        Vector3 _minPosition = new Vector3(float.MaxValue);
        Vector3 _maxPosition = new Vector3(float.MinValue);

        readonly GLtf.Builder _builder;
        readonly long _byteOffset;
        readonly long _buffer;
        readonly static int _floatByteSize = Marshal.SizeOf<float>();
        readonly static int _numFloats = (3 + 3 + 2);
        readonly static int _byteStride = _floatByteSize * _numFloats;

        internal VertexBufferBuilderPT(GLtf.Builder builder, long buffer = 0) :
            base(builder.Buffer, GLtf.VertexAttribute.POSITION, GLtf.VertexAttribute.NORMAL, GLtf.VertexAttribute.TEXCOORD_0_FLOAT)
        {
            _builder = builder;
            _byteOffset = builder.PadBuffer(4);
            _buffer = buffer;
        }

        public uint Count => _count;

        unsafe public uint Add(Vector3 pos, Vector3 norm, Vector2 uv)
        {
            var current = ValuesBuffer;
            current->Position = pos;
            current->Normal = norm;
            current->TexCoord_0 = uv;
            base.WriteValues();
            _minPosition = Vector3.Min(_minPosition, pos);
            _maxPosition = Vector3.Max(_maxPosition, pos);
            return _count++;
        }

        public (int positionAccessor, int normalAccessor, int uvAccessor) Finish()
        {
            var minPos = new System.DoubleNumerics.Vector3(_minPosition.X, _minPosition.Y, _minPosition.Z);
            var maxPos = new System.DoubleNumerics.Vector3(_maxPosition.X, _maxPosition.Y, _maxPosition.Z);
            var bufferView = _builder.AddBufferView(_buffer, _count * _byteStride, _byteOffset, _byteStride, GLtf.BufferTarget.ARRAY_BUFFER);
            var positionAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC3", bufferView, 0, minPos, maxPos);
            var normalAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC3", bufferView, 12);
            var uvAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC2", bufferView, 24);
            return (positionAccessor, normalAccessor, uvAccessor);
        }
    }


    class Program
    {
        static int Main(string[] args)
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var arguments = Arguments.Parse(args);
            if (arguments == null) return -1;
            arguments.OutputFolder.Create();
            MagickNET.SetGhostscriptDirectory(Directory.GetCurrentDirectory());
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // Console.Write("Loading.");
            //var settings = new CoordnateSettings();

            // using (StreamReader r = new StreamReader(args[1]))
            // {
            //     string json = r.ReadToEnd();
            //     settings = JsonConvert.DeserializeObject<CoordnateSettings>(json);
            // }

            PdfToImageConverter pdf = new PdfToImageConverter(arguments.File);
            pdf.ConvertFileToImages(arguments.File, arguments.OutputFolder.FullName, arguments.Density, arguments.TileSize);
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
        public void ConvertFileToImages(FileInfo file, string destinationPath, double initialDensity, int tileSize)
        {
            var fileName = file.Name;

            double currentDensity = initialDensity;
            ;
            var pageTresholdReached = new List<bool>();
            int lodDepth = 1;
            int filesWritten = 0;
            MagickReadSettings magickReadSettings = new MagickReadSettings();
            var lods = new List<MagickImageCollection>();
            while (true)//currentDensity > 10)
            {
                bool tresholdReached = true;
                magickReadSettings.Density = new Density(currentDensity, currentDensity);
                var collection = new MagickImageCollection();
                lods.Add(collection);
                collection.Read(file.ToString(), magickReadSettings);
                for (var pageIdx = 0; pageIdx < collection.Count; pageIdx++)
                {
                    var magickImage = collection[pageIdx];
                    magickImage.ColorAlpha(MagickColor.FromRgb(255, 255, 255));
                    if (pageIdx == pageTresholdReached.Count)
                    {
                        pageTresholdReached.Add(false);
                    }
                    if (pageTresholdReached[pageIdx])
                    {
                        continue;
                    }
                    if (magickImage.Height <= tileSize && magickImage.Width <= tileSize)
                    {
                        pageTresholdReached[pageIdx] = true;
                        continue;
                    }
                    tresholdReached = false;
                }
                if (tresholdReached)
                {
                    break;
                }
                currentDensity = currentDensity / 2;
            }

            var numPages = lods[0].Count;
            var zeroPages = Enumerable.Repeat(((byte[])null, (string)null, -1), numPages).ToArray();
            var numPageLods = (int)Math.Ceiling(Math.Log(numPages, 8));

            string pageFormat = null;

            using (var metadata = File.CreateText(Path.Combine(destinationPath, "metadata")))
            {
                if (numPages > 1)
                {
                    metadata.WriteLine($"{{\"id\":0,\"path\":\"{fileName}\",\"level\":0,\"type\":0,\"name\":\"{fileName}\",\"properties\":[]}}");
                    pageFormat = string.Concat(Enumerable.Repeat("0", (int)Math.Ceiling(Math.Log10(numPages + 1))));
                    for (var i = 0; i < numPages; i++)
                    {
                        var preview = $"{Path.GetFileNameWithoutExtension(fileName)}_{(i + 1).ToString(pageFormat)}.jpeg";
                        var width = lods[0][i].Width;
                        var height = lods[0][i].Height;
                        lods[Math.Max(0, lods.Count - 5)][i].Write(Path.Combine(destinationPath, preview), MagickFormat.Jpeg);
                        var _name = $"Page {(i + 1).ToString(pageFormat)}";
                        metadata.WriteLine($"{{\"id\":{i + 1},\"path\":\"{fileName}/{_name}\",\"level\":1,\"type\":1,\"name\":\"{_name}\",\"properties\":[[\"Novorender/Document/Size\",\"{width},{height}\"],[\"Novorender/Document/Preview\",\"{preview}\"]]}}");
                    }
                }
                else
                {
                    var preview = $"{Path.GetFileNameWithoutExtension(fileName)}.jpeg";
                    lods[Math.Max(0, lods.Count - 5)][0].Write(Path.Combine(destinationPath, preview), MagickFormat.Jpeg);
                    var width = lods[0][0].Width;
                    var height = lods[0][0].Height;
                    // lods[Math.Max(0, lods.Count - 4)][0].Write(tmpFile, MagickFormat.Jpeg);
                    // var data = Convert.ToBase64String(System.IO.File.ReadAllBytes(tmpFile));
                    // var textUri = $"data:image/jpeg;base64,{data}";
                    metadata.WriteLine($"{{\"id\":0,\"path\":\"{fileName}\",\"level\":0,\"type\":1,\"name\":\"{fileName}\",\"properties\":[[\"Novorender/Document/Size\",\"{width},{height}\"],[\"Novorender/Document/Preview\",\"{preview}\"]]}}");
                }
            }

            var normal = new Vector3(0, 0, 1);

            foreach (var magickImageCollection in lods)
            {
                int pageIdx = 0;
                foreach (MagickImage magickImage in magickImageCollection)
                {
                    var pagePrefix = "";
                    if (numPages > 1)
                    {
                        var pi = pageIdx;
                        for (var i = 0; i < numPageLods; i++)
                        {
                            pagePrefix = $"{(pi % 8)}{pagePrefix}";
                            pi /= 8;
                        }
                    }
                    var pixels = magickImage.GetPixels();
                    var maxSize = (double)Math.Max(magickImage.Width, magickImage.Height);
                    var noIteration = (int)Math.Pow(2, Math.Ceiling(Math.Log2(Math.Ceiling(maxSize / (double)tileSize))));
                    var wsD = (double)tileSize / (double)magickImage.Height;
                    for (var i = 0; i < noIteration; ++i)
                    {
                        int x = i * tileSize;
                        if (x >= magickImage.Width) break;
                        int endWidth = x + tileSize;
                        int dx = endWidth > magickImage.Width ? magickImage.Width - x : tileSize;
                        for (var j = 0; j < noIteration; ++j)
                        {
                            int y = j * tileSize;
                            if (y >= magickImage.Height) break;
                            var id = new string(Enumerable.Range(0, lods.Count - lodDepth).Select(k => (char)('0' + ((i & (1 << k)) != 0 ? 1 : 0) | ((j & (1 << k)) != 0 ? 2 : 0))).Reverse().ToArray());
                            var glbFilename = $"{destinationPath}/_{pagePrefix}{id}";
                            using (var builder = new GLtf.Builder(glbFilename))
                            {
                                Console.WriteLine(glbFilename);

                                int endHeight = y + tileSize;
                                int dy = endHeight > magickImage.Height ? magickImage.Height - y : tileSize;
                                var pixelArea = pixels.GetArea(x, y, dx, dy);
                                var tiledImage = new MagickImage();
                                var settings = new PixelReadSettings(dx, dy, StorageType.Quantum, PixelMapping.RGB);
                                tiledImage.ReadPixels(pixelArea.AsSpan(), settings);
                                if (dx != tileSize || dy != tileSize)
                                {
                                    var mg = new MagickGeometry(tileSize) { IgnoreAspectRatio = true };
                                    tiledImage.Resize(mg);
                                }
                                tiledImage.Write(Path.Combine(destinationPath, $"{Path.GetFileNameWithoutExtension(fileName)}"
                                    + (string.IsNullOrWhiteSpace(pageFormat) ? "" : $"_{(pageIdx + 1).ToString(pageFormat)}")
                                    + $"_{id}.jpeg"));
                                tiledImage.Format = MagickFormat.Png;
                                tiledImage.Write(tmpFile);

                                var imgBlob = System.IO.File.ReadAllBytes(tmpFile);
                                if (lods.Count == lodDepth) zeroPages[pageIdx] = (imgBlob, pagePrefix, pageIdx + 1);
                                var (bufferBegin, bufferEnd) = builder.Buffer.AddRange(imgBlob);
                                var imageBufferView = builder.AddBufferView(0, imgBlob.Length, bufferBegin);
                                var imgIdx = builder.AddImage("image/png", imageBufferView);
                                var baseTexture = builder.AddTexture(imgIdx, null);

                                var material = builder.AddUnlitMaterial(rgba: new System.DoubleNumerics.Vector4(1, 1, 1, 1), alphaMode: GLtf.AlphaMode.BLEND, baseColorTexture: builder.CreateTextureInfo(baseTexture), doubleSided: true);

                                using (var vertexBuffer = new VertexBufferBuilderPT(builder))
                                {
                                    var _dx = (float)dx / (float)tileSize;
                                    var _dy = (float)dy / (float)tileSize;
                                    var width = (float)(wsD * _dx);
                                    var height = (float)(wsD * _dy);
                                    var localCornerX = i * wsD;
                                    var localCornerY = 1 - j * wsD - height;
                                    var z = pageIdx * -0.0001f;
                                    Vector3 a = new Vector3((float)localCornerX, (float)localCornerY, z);
                                    Vector3 b = new Vector3((float)localCornerX + width, (float)localCornerY, z);
                                    Vector3 c = new Vector3((float)localCornerX + width, (float)localCornerY + height, z);
                                    Vector3 d = new Vector3((float)localCornerX, (float)localCornerY + height, z);
                                    vertexBuffer.Add(a, normal, new Vector2(0, 1));
                                    vertexBuffer.Add(b, normal, new Vector2(1, 1));
                                    vertexBuffer.Add(c, normal, new Vector2(1, 0));

                                    vertexBuffer.Add(a, normal, new Vector2(0, 1));
                                    vertexBuffer.Add(c, normal, new Vector2(1, 0));
                                    vertexBuffer.Add(d, normal, new Vector2(0, 0));

                                    var (positionAccessor, normalAccessor, uvAccessor) = vertexBuffer.Finish();
                                    var attributes = new[]
                                    {
                                            builder.CreateAttribute("POSITION", positionAccessor),
                                            builder.CreateAttribute("NORMAL", normalAccessor),
                                            builder.CreateAttribute("TEXCOORD_0", uvAccessor),
                                        };
                                    var primitive = builder.CreatePrimitive(attributes, mode: GLtf.DrawMode.TRIANGLES, material: material);
                                    var meshIdx = builder.AddMesh(new[] { primitive });
                                    var nodeIdx = builder.AddNode(name: $"{(numPages > 1 ? pageIdx + 1 : 0)}", mesh: meshIdx);
                                    builder.AddScene(nodes: new[] { nodeIdx });
                                    builder.Write(true);
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
            var _aspect = (double)lods[0][0].Width / (double)lods[0][0].Height;
            void save(string id, (byte[] blob, string prefix, int id)[] pages)
            {
                var glbFilename = $"{destinationPath}/{id}";
                using (var builder = new GLtf.Builder(glbFilename))
                {
                    GLtf.Attribute[] attributes;
                    using (var vertexBuffer = new VertexBufferBuilderPT(builder))
                    {
                        Vector3 a = new Vector3(0, 0, 0);
                        Vector3 b = new Vector3((float)_aspect, 0, 0);
                        Vector3 c = new Vector3((float)_aspect, 1, 0);
                        Vector3 d = new Vector3(0, 1, 0);
                        vertexBuffer.Add(a, normal, new Vector2(0, 1));
                        vertexBuffer.Add(b, normal, new Vector2(1, 1));
                        vertexBuffer.Add(c, normal, new Vector2(1, 0));

                        vertexBuffer.Add(a, normal, new Vector2(0, 1));
                        vertexBuffer.Add(c, normal, new Vector2(1, 0));
                        vertexBuffer.Add(d, normal, new Vector2(0, 0));

                        var (positionAccessor, normalAccessor, uvAccessor) = vertexBuffer.Finish();
                        attributes = new[]
                        {
                            builder.CreateAttribute("POSITION", positionAccessor),
                            builder.CreateAttribute("NORMAL", normalAccessor),
                            builder.CreateAttribute("TEXCOORD_0", uvAccessor),
                        };
                    }
                    var nodes = pages.Select((page, i) =>
                    {
                        var (bufferBegin, bufferEnd) = builder.Buffer.AddRange(page.blob);
                        var imageBufferView = builder.AddBufferView(0, page.blob.Length, bufferBegin);
                        var imgIdx = builder.AddImage("image/png", imageBufferView);
                        var baseTexture = builder.AddTexture(imgIdx, null);

                        var material = builder.AddUnlitMaterial(rgba: new System.DoubleNumerics.Vector4(1, 1, 1, 1), alphaMode: GLtf.AlphaMode.BLEND, baseColorTexture: builder.CreateTextureInfo(baseTexture), doubleSided: true);

                        var primitive = builder.CreatePrimitive(attributes, mode: GLtf.DrawMode.TRIANGLES, material: material);
                        var meshIdx = builder.AddMesh(new[] { primitive });
                        return builder.AddNode(name: page.id.ToString(), mesh: meshIdx);
                    }).ToArray();
                    builder.AddScene(nodes);
                    builder.Write(true);
                }
                foreach (var pg in pages.Where(p => p.prefix.Length > 1).GroupBy(p => p.prefix[0]))
                {
                    save($"{id}{pg.Key}", pg.Select(p => (p.blob, p.prefix.Substring(1), p.id)).ToArray());
                }
            }
            if (numPages > 1)
            {
                save("_", zeroPages);
            }
            {
                var image = lods[Math.Max(0, lods.Count - 5)][0];
                var x = (int)Math.Min(2048, Math.Pow(2, Math.Log2((double)image.Width)));
                var y = (int)Math.Min(2048, Math.Pow(2, Math.Log2((double)image.Height)));
                var mg = new MagickGeometry(x, y) { IgnoreAspectRatio = true };
                image.Resize(mg);
                image.Write(tmpFile, MagickFormat.Png);
                save("asset", new[] { (System.IO.File.ReadAllBytes(tmpFile), "", 0) });
            }
            File.WriteAllText(destinationPath + "/asset.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "document",
                levels = lodDepth,
                bounds = new
                {
                    box = new
                    {
                        Min = new
                        {
                            X = 0,
                            Y = 0,
                            Z = 0
                        },
                        Max = new
                        {
                            X = _aspect,
                            Y = 1,
                            Z = 0
                        }
                    },
                    sphere = new
                    {
                        Center = new
                        {
                            X = _aspect * 0.5,
                            Y = 0.5,
                            Z = 0
                        },
                        Radius = Math.Sqrt(0.25 + 0.25 * _aspect * _aspect)
                    }
                },
                parsers = new [] {new [] {"pdf_reader", "1.0"}}
            }));
            string[] tmpFiles = Directory.GetFiles(tmpDir.ToString());
            foreach (var tmpFile in tmpFiles)
            {
                File.Delete(tmpFile);
            }
            Directory.Delete(tmpDir.ToString());
        }
    }
}

