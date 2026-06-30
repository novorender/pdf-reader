using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using GLtf = Novorender.GLtf;
using ImageMagick;
using PDFiumCore;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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
            var minPos = new Speckle.DoubleNumerics.Vector3(_minPosition.X, _minPosition.Y, _minPosition.Z);
            var maxPos = new Speckle.DoubleNumerics.Vector3(_maxPosition.X, _maxPosition.Y, _maxPosition.Z);
            var bufferView = _builder.AddBufferView(_buffer, _count * _byteStride, _byteOffset, _byteStride, GLtf.BufferTarget.ARRAY_BUFFER);
            var positionAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC3", bufferView, 0, minPos, maxPos);
            var normalAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC3", bufferView, 12);
            var uvAccessor = _builder.AddAccessor(GLtf.ComponentType.FLOAT, _count, "VEC2", bufferView, 24);
            return (positionAccessor, normalAccessor, uvAccessor);
        }
    }


    class Program
    {
        private const string EpsgPrefix = "EPSG:";

        private const int Wgs84Epsg = 4326;

        static int Main(string[] args)
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var arguments = Arguments.Parse(args);
            if (arguments == null) return -1;
            arguments.OutputFolder.Create();
            var timer = System.Diagnostics.Stopwatch.StartNew();

            PdfToImageConverter pdf = new PdfToImageConverter(arguments.File);
            pdf.ConvertFileToImages(arguments.File, arguments.OutputFolder.FullName, arguments.Density, arguments.TileSize, GetEpsgCode(arguments.Epsg));
            timer.Stop();
            Console.WriteLine($"Write complete in {timer.Elapsed}");
            return 0;
        }

        private static int GetEpsgCode(ReadOnlySpan<char> epsg)
        {
            if (epsg.IsEmpty)
            {
                return Wgs84Epsg;
            }

            if (epsg.StartsWith(EpsgPrefix))
            {
                epsg = epsg[EpsgPrefix.Length..];
            }

            if (int.TryParse(epsg, out var epsgCode))
            {
                return epsgCode;
            }

            throw new ArgumentException($"Invalid EPSG code: {epsg.ToString()}");
        }
    }

    public class PdfToImageConverter
    {
        DirectoryInfo tmpDir;

        static readonly object _pdfiumLock = new object();
        static bool _pdfiumInitialized;

        // PDFium's FPDF_InitLibrary must be called exactly once per process (and the matching
        // FPDF_DestroyLibrary is intentionally not called per-conversion — that would break a
        // second call; the library is torn down on process exit).
        static void EnsurePdfiumInitialized()
        {
            if (_pdfiumInitialized) return;
            lock (_pdfiumLock)
            {
                if (_pdfiumInitialized) return;
                fpdfview.FPDF_InitLibrary();
                _pdfiumInitialized = true;
            }
        }

        public PdfToImageConverter(FileInfo inputFile)
        {
            tmpDir = new DirectoryInfo(Path.Combine(inputFile.Directory.FullName, "Tmp"));
            Directory.CreateDirectory(tmpDir.FullName);
            MagickNET.SetTempDirectory(tmpDir.FullName);
        }

        /// <summary>
        /// Builds the model-tree metadata rows (one JSON object per line) for a document.
        /// Single-page => one leaf row (empty path; the consumer names it after the file).
        /// Multi-page  => a container row (type 0, empty path) followed by one leaf per page
        /// with numeric path "1".."n" and display name "Page N", so the downstream pipeline
        /// produces file.pdf (container) + file.pdf/1..n (leaves).
        /// </summary>
        public static IEnumerable<string> BuildMetadataLines(
            int numPages, string documentId, (uint width, uint height)[] sizes, string[] previews)
        {
            if (numPages > 1)
            {
                // File container node (no page image of its own); id placed after the pages to stay unique.
                yield return $"{{\"id\":{numPages},\"path\":\"\",\"level\":0,\"type\":0,\"name\":\"\",\"properties\":[[\"Procore/Id\",\"{documentId}\"]]}}";
                for (var i = 0; i < numPages; i++)
                {
                    // Numeric path (file.pdf/1..n) with a "Page N" display name.
                    var name = $"Page {i + 1}";
                    yield return $"{{\"id\":{i},\"path\":\"{i + 1}\",\"level\":1,\"type\":1,\"name\":\"{name}\",\"properties\":[[\"Procore/Id\",\"{documentId}_{i}\"],[\"Novorender/Document/Size\",\"{sizes[i].width},{sizes[i].height}\"],[\"Novorender/Document/Preview\",\"{previews[i]}\"]]}}";
                }
            }
            else
            {
                yield return $"{{\"id\":0,\"path\":\"\",\"level\":0,\"type\":1,\"name\":\"\",\"properties\":[[\"Procore/Id\",\"{documentId}\"],[\"Novorender/Document/Size\",\"{sizes[0].width},{sizes[0].height}\"],[\"Novorender/Document/Preview\",\"{previews[0]}\"]]}}";
            }
        }

        public void ConvertFileToImages(FileInfo file, string destinationPath, double initialDensity, uint tileSize, int epsg)
        {
            var fileName = file.Name;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var documentId = PdfDocumentId.GetDocumentId(file);

            int lodDepth = 1;
            var density = initialDensity;
            // Embedded GLB textures are PNG (lossless), matching the previous Ghostscript pipeline.
            var texFmt = MagickFormat.Png;
            var texMime = "image/png";
            var pdfBytes = File.ReadAllBytes(file.FullName);

            EnsurePdfiumInitialized();
            var pdfHandle = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
            var doc = fpdfview.FPDF_LoadMemDocument(pdfHandle.AddrOfPinnedObject(), pdfBytes.Length, null);
            if (doc == null)
            {
                pdfHandle.Free();
                throw new InvalidOperationException(
                    $"PDFium failed to load '{file.FullName}' (corrupt or password-protected). Error code: {fpdfview.FPDF_GetLastError()}");
            }
            var numPages = fpdfview.FPDF_GetPageCount(doc);
            var pages = new FpdfPageT[numPages];
            var pagePtsW = new double[numPages];
            var pagePtsH = new double[numPages];
            for (int p = 0; p < numPages; p++)
            {
                pages[p] = fpdfview.FPDF_LoadPage(doc, p);
                pagePtsW[p] = fpdfview.FPDF_GetPageWidthF(pages[p]);
                pagePtsH[p] = fpdfview.FPDF_GetPageHeightF(pages[p]);
            }

            // Native PDFium handles + the pinned GC handle are released in the finally below,
            // so they don't leak if conversion throws.
            try
            {

            // Pixel dimensions of a page at LOD k (density halves each level).
            int Dim(double pts, double d) => (int)Math.Round(pts * d / 72.0);
            uint LevelW(int p, int k) => (uint)Dim(pagePtsW[p], density / Math.Pow(2, k));
            uint LevelH(int p, int k) => (uint)Dim(pagePtsH[p], density / Math.Pow(2, k));

            // Render a region of a page (the page scaled to sizeX x sizeY, offset by start)
            // into a contiguous outW x outH BGRA byte buffer, composited over white (the
            // bitmap is filled opaque white before rendering). PDFium is single-threaded, so
            // every render call must happen serially.
            byte[] RenderRegionRaw(FpdfPageT page, int startX, int startY, int sizeX, int sizeY, uint outW, uint outH)
            {
                var bmp = fpdfview.FPDFBitmapCreate((int)outW, (int)outH, 1);
                fpdfview.FPDFBitmapFillRect(bmp, 0, 0, (int)outW, (int)outH, 0xFFFFFFFFUL);
                fpdfview.FPDF_RenderPageBitmap(bmp, page, startX, startY, sizeX, sizeY, 0, 0);
                int stride = fpdfview.FPDFBitmapGetStride(bmp);
                IntPtr buf = fpdfview.FPDFBitmapGetBuffer(bmp);
                var bytes = new byte[(int)outW * 4 * (int)outH];
                for (int row = 0; row < outH; row++)
                    Marshal.Copy(buf + row * stride, bytes, row * (int)outW * 4, (int)outW * 4);
                fpdfview.FPDFBitmapDestroy(bmp);
                return bytes;
            }

            // MagickImage variant (used for the preview/thumbnail renders).
            MagickImage RenderRegion(FpdfPageT page, int startX, int startY, int sizeX, int sizeY, uint outW, uint outH)
            {
                var img = new MagickImage();
                img.ReadPixels(RenderRegionRaw(page, startX, startY, sizeX, sizeY, outW, outH),
                    new PixelReadSettings(outW, outH, StorageType.Char, PixelMapping.BGRA));
                img.ColorAlpha(MagickColor.FromRgb(255, 255, 255));
                return img;
            }

            // Number of LOD levels: keep halving density until the largest page fits a tile.
            uint maxDim = 0;
            for (int p = 0; p < numPages; p++)
                maxDim = Math.Max(maxDim, Math.Max(LevelW(p, 0), LevelH(p, 0)));
            int levelCount = maxDim <= tileSize ? 1 : (int)Math.Ceiling(Math.Log2((double)maxDim / tileSize)) + 1;

            var zeroPages = Enumerable.Repeat(((byte[])null, (string)null, -1), numPages).ToArray();
            var numPageLods = (int)Math.Ceiling(Math.Log(numPages, 8));

            string pageFormat = null;

            // Write the per-page preview JPEGs and collect page sizes/preview names, then emit the
            // model-tree metadata via BuildMetadataLines (container row + leaves for multi-page).
            var sizes = new (uint width, uint height)[numPages];
            var previews = new string[numPages];
            if (numPages > 1)
            {
                pageFormat = string.Concat(Enumerable.Repeat("0", (int)Math.Ceiling(Math.Log10(numPages + 1))));
                int previewLevel = Math.Max(0, levelCount - 5);
                for (var i = 0; i < numPages; i++)
                {
                    var preview = $"{baseName}_{(i + 1).ToString(pageFormat)}.jpeg";
                    sizes[i] = (LevelW(i, 0), LevelH(i, 0));
                    previews[i] = preview;
                    using var pv = RenderRegion(pages[i], 0, 0, (int)LevelW(i, previewLevel), (int)LevelH(i, previewLevel), LevelW(i, previewLevel), LevelH(i, previewLevel));
                    pv.Write(Path.Combine(destinationPath, preview), MagickFormat.Jpeg);
                }
            }
            else
            {
                var preview = $"{baseName}.jpeg";
                int previewLevel = Math.Max(0, levelCount - 7);
                using var pv = RenderRegion(pages[0], 0, 0, (int)LevelW(0, previewLevel), (int)LevelH(0, previewLevel), LevelW(0, previewLevel), LevelH(0, previewLevel));
                pv.Write(Path.Combine(destinationPath, preview), MagickFormat.Jpeg);
                sizes[0] = (LevelW(0, 0), LevelH(0, 0));
                previews[0] = preview;
            }

            using (var metadata = File.CreateText(Path.Combine(destinationPath, "metadata")))
            {
                foreach (var line in BuildMetadataLines(numPages, documentId, sizes, previews))
                {
                    metadata.WriteLine(line);
                }
            }

            var normal = new Vector3(0, 0, 1);

            for (int k = 0; k < levelCount; k++)
            {
                bool isCoarsestLod = lodDepth == levelCount;
                int idBits = levelCount - lodDepth;
                for (int pageIdx = 0; pageIdx < numPages; pageIdx++)
                {
                    var page = pages[pageIdx];
                    uint Wk = LevelW(pageIdx, k);
                    uint Hk = LevelH(pageIdx, k);
                    var pagePrefix = "";
                    if (numPages > 1)
                    {
                        var pi = pageIdx;
                        for (var b = 0; b < numPageLods; b++)
                        {
                            pagePrefix = $"{(pi % 8)}{pagePrefix}";
                            pi /= 8;
                        }
                    }
                    int nx = (int)((Wk + tileSize - 1) / tileSize);
                    int ny = (int)((Hk + tileSize - 1) / tileSize);
                    var wsD = (double)tileSize / (double)Hk;
                    var prefix = pagePrefix;
                    var pageId = pageIdx;

                    // Pipeline the serial PDFium render with the parallel tile encode: one
                    // producer thread renders strips (PDFium must stay serial / single-thread)
                    // into a small bounded queue, while the consumer encodes the previous
                    // strip's tiles in parallel. This keeps the cores busy during the
                    // otherwise-idle render, and still bounds memory to ~2 strips.
                    var bandQueue = new System.Collections.Concurrent.BlockingCollection<(int jj, byte[] bgra, uint bandH)>(2);
                    var renderTask = Task.Run(() =>
                    {
                        try
                        {
                            for (int j = 0; j < ny; j++)
                            {
                                uint bandY = (uint)j * tileSize;
                                uint bh = bandY + tileSize > Hk ? Hk - bandY : tileSize;
                                bandQueue.Add((j, RenderRegionRaw(page, 0, -(int)bandY, (int)Wk, (int)Hk, Wk, bh), bh));
                            }
                        }
                        finally { bandQueue.CompleteAdding(); }
                    });

                    foreach (var (jj, bandBgra, bandH) in bandQueue.GetConsumingEnumerable())
                    {
                        int bandStride = (int)Wk * 4;

                        Parallel.For(0, nx, ii =>
                        {
                            uint i = (uint)ii;
                            uint x = i * tileSize;
                            uint dx = x + tileSize > Wk ? Wk - x : tileSize;
                            uint dy = bandH;
                            var id = new string(Enumerable.Range(0, idBits).Select(bit => (char)('0' + ((i & (1 << bit)) != 0 ? 1 : 0) | (((uint)jj & (1 << bit)) != 0 ? 2 : 0))).Reverse().ToArray());
                            var glbFilename = $"{destinationPath}/_{prefix}{id}";
                            using var builder = new GLtf.Builder(glbFilename);
                            using var tiledImage = new MagickImage();
                            // Copy this tile's region out of the shared BGRA strip. Reads of a
                            // plain byte[] are thread-safe, so no lock is needed; each tile owns
                            // its own buffer + MagickImage.
                            int rowBytes = (int)dx * 4;
                            var tileBgra = new byte[rowBytes * (int)dy];
                            for (int row = 0; row < dy; row++)
                                Array.Copy(bandBgra, row * bandStride + (int)x * 4, tileBgra, row * rowBytes, rowBytes);
                            tiledImage.ReadPixels(tileBgra, new PixelReadSettings(dx, dy, StorageType.Char, PixelMapping.BGRA));
                            tiledImage.ColorAlpha(MagickColor.FromRgb(255, 255, 255));
                            if (dx != tileSize || dy != tileSize)
                            {
                                var mg = new MagickGeometry(tileSize) { IgnoreAspectRatio = true };
                                tiledImage.Resize(mg);
                            }
                            tiledImage.Write(Path.Combine(destinationPath, $"{baseName}"
                                + (string.IsNullOrWhiteSpace(pageFormat) ? "" : $"_{(pageId + 1).ToString(pageFormat)}")
                                + $"_{id}.jpeg"));

                            var imgBlob = tiledImage.ToByteArray(texFmt);
                            // The coarsest LOD is a single tile per page; retain it for the
                            // page-group GLBs assembled after this loop.
                            if (isCoarsestLod) zeroPages[pageId] = (imgBlob, prefix, pageId);
                            var (bufferBegin, _) = builder.Buffer.AddRange(imgBlob);
                            var imageBufferView = builder.AddBufferView(0, imgBlob.Length, bufferBegin);
                            var imgIdx = builder.AddImage(texMime, imageBufferView);
                            var baseTexture = builder.AddTexture(imgIdx, null);

                            var material = builder.AddUnlitMaterial(rgba: new Speckle.DoubleNumerics.Vector4(1, 1, 1, 1), alphaMode: GLtf.AlphaMode.BLEND, baseColorTexture: builder.CreateTextureInfo(baseTexture), doubleSided: true);

                            using (var vertexBuffer = new VertexBufferBuilderPT(builder))
                            {
                                var _dx = (float)dx / (float)tileSize;
                                var _dy = (float)dy / (float)tileSize;
                                var width = (float)(wsD * _dx);
                                var height = (float)(wsD * _dy);
                                var localCornerX = i * wsD;
                                var localCornerY = 1 - jj * wsD - height;
                                var z = pageId * -0.0001f;
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
                                var nodeIdx = builder.AddNode(name: pageId.ToString(), mesh: meshIdx);
                                builder.AddScene(nodes: new[] { nodeIdx });
                                builder.Write(true);
                            }
                        });
                    }
                    renderTask.Wait();
                }
                ++lodDepth;
            }
            var _aspect = (double)LevelW(0, 0) / (double)LevelH(0, 0);
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
                        var imgIdx = builder.AddImage(texMime, imageBufferView);
                        var baseTexture = builder.AddTexture(imgIdx, null);

                        var material = builder.AddUnlitMaterial(rgba: new Speckle.DoubleNumerics.Vector4(1, 1, 1, 1), alphaMode: GLtf.AlphaMode.BLEND, baseColorTexture: builder.CreateTextureInfo(baseTexture), doubleSided: true);

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
                int al = Math.Max(0, levelCount - 5);
                using var image = RenderRegion(pages[0], 0, 0, (int)LevelW(0, al), (int)LevelH(0, al), LevelW(0, al), LevelH(0, al));
                var x = (uint)Math.Min(2048, Math.Pow(2, Math.Log2(image.Width)));
                var y = (uint)Math.Min(2048, Math.Pow(2, Math.Log2(image.Height)));
                var mg = new MagickGeometry(x, y) { IgnoreAspectRatio = true };
                image.Resize(mg);
                save("asset", new[] { (image.ToByteArray(texFmt), "", 0) });
            }
            File.WriteAllText(destinationPath + "/asset.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                version = "1.0",
                type = "document",
                levels = lodDepth,
                objects = numPages,
                geometryTypes = 2, //PDF
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
                parsers = new[] { new[] { "pdf_reader", "1.0" } }
            }));

            }
            finally
            {
                for (int p = 0; p < numPages; p++) fpdfview.FPDF_ClosePage(pages[p]);
                fpdfview.FPDF_CloseDocument(doc);
                pdfHandle.Free();
            }

            // Cleanup temporary files
            try
            {
                string[] tmpFiles = Directory.GetFiles(tmpDir.FullName);
                foreach (var tempFilePath in tmpFiles)
                {
                    File.Delete(tempFilePath);
                }
                Directory.Delete(tmpDir.FullName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to cleanup temp directory: {ex.Message}");
            }
        }
    }
}
