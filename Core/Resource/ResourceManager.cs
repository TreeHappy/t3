using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using SharpDX.WIC;
using T3.Core.Logging;
using T3.Core.Operator;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

// ReSharper disable SuggestVarOrType_SimpleTypes <- Cynic doesn't like it
// ReSharper disable PrivateFieldCanBeConvertedToLocalVariable    <- keeping the file handlers as members is clearer

namespace T3.Core
{
    public interface IUpdateable
    {
        void Update(string path);
    }
    
    
    public class ResourceManager
    {
        public Texture2D SecondRenderWindowTexture { get; set; }

        public Assembly OperatorsAssembly { get; set; }

        public static ResourceManager Instance()
        {
            return _instance;
        }

        public T GetResource<T>(uint resourceId) where T : Resource
        {
            return (T)Resources[resourceId];
        }

        public VertexShader GetVertexShader(uint resourceId)
        {
            return GetResource<VertexShaderResource>(resourceId).VertexShader;
        }

        public PixelShader GetPixelShader(uint resourceId)
        {
            return GetResource<PixelShaderResource>(resourceId).PixelShader;
        }

        public ComputeShader GetComputeShader(uint resourceId)
        {
            return GetResource<ComputeShaderResource>(resourceId).ComputeShader;
        }
        
        public GeometryShader GetGeometryShader(uint resourceId)
        {
            return GetResource<GeometryShaderResource>(resourceId).GeometryShader;
        }

        public ShaderBytecode GetComputeShaderBytecode(uint resourceId)
        {
            return GetResource<ComputeShaderResource>(resourceId).Blob;
        }
        
        public OperatorResource GetOperatorFileResource(string path)
        {
            bool foundFileEntryForPath = _fileResources.TryGetValue(path, out var fileResource);
            if (foundFileEntryForPath)
            {
                foreach (var id in fileResource.ResourceIds)
                {
                    if (Resources[id] is OperatorResource opResource)
                        return opResource;
                }
            }

            return null;
        }

        public void RenameOperatorResource(string oldPath, string newPath)
        {
            var extension = Path.GetExtension(newPath);
            if (extension != ".cs")
            {
                Log.Info($"Ignoring file rename to invalid extension '{extension}' in '{newPath}'.");
                return;
            }

            if (_fileResources.TryGetValue(oldPath, out var fileResource))
            {
                Log.Info($"renamed file resource from '{oldPath}' to '{newPath}'");
                fileResource.Path = newPath;
                _fileResources.Remove(oldPath);
                _fileResources.Add(newPath, fileResource);
            }
        }

        public const uint NullResource = 0;
        private uint _resourceIdCounter = 1;

        private uint GetNextResourceId()
        {
            return _resourceIdCounter++;
        }

        public static void Init(Device device)
        {
            if (_instance == null)
                _instance = new ResourceManager(device);
        }

        private static ResourceManager _instance;

        private ResourceManager(Device device)
        {
            Device = device;

            var samplerDesc = new SamplerStateDescription()
                                  {
                                      Filter = Filter.MinMagMipLinear,
                                      AddressU = TextureAddressMode.Clamp,
                                      AddressV = TextureAddressMode.Clamp,
                                      AddressW = TextureAddressMode.Clamp,
                                      MipLodBias = 0.0f,
                                      MaximumAnisotropy = 1,
                                      ComparisonFunction = Comparison.Never,
                                      BorderColor = new RawColor4(1.0f, 1.0f, 1.0f, 1.0f),
                                      MinimumLod = -Single.MaxValue,
                                      MaximumLod = Single.MaxValue,
                                  };
            DefaultSamplerState = new SamplerState(device, samplerDesc);
                
            _hlslFileWatcher = new FileSystemWatcher(ResourcesFolder, "*.hlsl");
            _hlslFileWatcher.IncludeSubdirectories = true;
            _hlslFileWatcher.Changed += OnChanged;
            _hlslFileWatcher.Created += OnChanged;
            _hlslFileWatcher.Deleted += OnChanged;
            _hlslFileWatcher.Renamed += OnChanged;
            _hlslFileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime; // creation time needed for visual studio (2017)
            _hlslFileWatcher.EnableRaisingEvents = true;

            _pngFileWatcher = new FileSystemWatcher(ResourcesFolder, "*.png");
            _pngFileWatcher.IncludeSubdirectories = true;
            _pngFileWatcher.Changed += OnChanged;
            _pngFileWatcher.Created += OnChanged;
            _pngFileWatcher.EnableRaisingEvents = true;

            _jpgFileWatcher = new FileSystemWatcher(ResourcesFolder, "*.jpg");
            _jpgFileWatcher.IncludeSubdirectories = true;
            _jpgFileWatcher.Changed += OnChanged;
            _jpgFileWatcher.Created += OnChanged;
            _jpgFileWatcher.EnableRaisingEvents = true;

            _ddsFileWatcher = new FileSystemWatcher(ResourcesFolder, "*.dds");
            _ddsFileWatcher.IncludeSubdirectories = true;
            _ddsFileWatcher.Changed += OnChanged;
            _ddsFileWatcher.Created += OnChanged;
            _ddsFileWatcher.EnableRaisingEvents = true;

            _tiffFileWatcher = new FileSystemWatcher(ResourcesFolder, "*.tiff");
            _tiffFileWatcher.IncludeSubdirectories = true;
            _tiffFileWatcher.Changed += OnChanged;
            _tiffFileWatcher.Created += OnChanged;
            _tiffFileWatcher.EnableRaisingEvents = true;

            _csFileWatcher = new FileSystemWatcher(Model.OperatorTypesFolder, "*.cs");
            _csFileWatcher.IncludeSubdirectories = true;
            _csFileWatcher.Changed += OnChanged;
            _csFileWatcher.Renamed += OnRenamed;
            _csFileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName;
            _csFileWatcher.EnableRaisingEvents = true;
        }

        public SamplerState DefaultSamplerState { get; }

        public void DisableOperatorFileWatcher()
        {
            _csFileWatcher.EnableRaisingEvents = false;
        }
        
        public void EnableOperatorFileWatcher()
        {
            _csFileWatcher.EnableRaisingEvents = true;
        }

        public void SetupConstBuffer<T>(T bufferData, ref Buffer buffer) where T : struct
        {
            using (var data = new DataStream(Marshal.SizeOf(typeof(T)), true, true))
            {
                data.Write(bufferData);
                data.Position = 0;

                if (buffer == null)
                {
                    var bufferDesc = new BufferDescription
                                     {
                                         Usage = ResourceUsage.Default,
                                         SizeInBytes = Marshal.SizeOf(typeof(T)),
                                         BindFlags = BindFlags.ConstantBuffer
                                     };
                    buffer = new Buffer(Device, data, bufferDesc);
                }
                else
                {
                    Device.ImmediateContext.UpdateSubresource(new DataBox(data.DataPointer, 0, 0), buffer);
                }
            }
        }

        public void SetupBuffer(BufferDescription bufferDesc, ref Buffer buffer)
        {
            if (buffer == null)
            {
                buffer = new Buffer(Device, bufferDesc);
            }
        }

        public void SetupIndirectBuffer(int sizeInBytes, ref Buffer buffer)
        {
            var bufferDesc = new BufferDescription
                             {
                                 Usage = ResourceUsage.Default,
                                 BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                                 SizeInBytes = sizeInBytes,
                                 OptionFlags = ResourceOptionFlags.DrawIndirectArguments,
                             };
            SetupBuffer(bufferDesc, ref buffer);
        }

        public void CreateBufferUav<T>(Buffer buffer, Format format, ref UnorderedAccessView uav)
        {
            if (buffer == null)
                return;

            if ((buffer.Description.OptionFlags & ResourceOptionFlags.BufferStructured) != 0)
            {
                Log.Warning($"Input buffer is structured, skipping UAV creation.");
                return;
            }

            uav?.Dispose();
            var desc = new UnorderedAccessViewDescription
                       {
                           Dimension = UnorderedAccessViewDimension.Buffer,
                           Format = format,
                           Buffer = new UnorderedAccessViewDescription.BufferResource()
                                    {
                                        FirstElement = 0,
                                        ElementCount = buffer.Description.SizeInBytes / Marshal.SizeOf<T>(),
                                        Flags = UnorderedAccessViewBufferFlags.None
                                    }
                       };
            uav = new UnorderedAccessView(Device, buffer, desc);
        }

        public void SetupStructuredBuffer<T>(T[] bufferData, ref Buffer buffer) where T : struct
        {
            int stride = Marshal.SizeOf(typeof(T));
            int sizeInBytes = stride * bufferData.Length;
            SetupStructuredBuffer(bufferData, sizeInBytes, stride, ref buffer);
        }

        public void SetupStructuredBuffer<T>(T[] bufferData, int sizeInBytes, int stride, ref Buffer buffer) where T : struct
        {
            using (var data = new DataStream(sizeInBytes, true, true))
            {
                data.WriteRange(bufferData);
                data.Position = 0;

                SetupStructuredBuffer(data, sizeInBytes, stride, ref buffer);
            }
        }

        public void SetupStructuredBuffer(DataStream data, int sizeInBytes, int stride, ref Buffer buffer) 
        {
            if (buffer == null || buffer.Description.SizeInBytes != sizeInBytes)
            {
                buffer?.Dispose();
                var bufferDesc = new BufferDescription
                                     {
                                         Usage = ResourceUsage.Default,
                                         BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                                         SizeInBytes = sizeInBytes,
                                         OptionFlags = ResourceOptionFlags.BufferStructured,
                                         StructureByteStride = stride
                                     };
                buffer = new Buffer(Device, data, bufferDesc);
            }
            else
            {
                Device.ImmediateContext.UpdateSubresource(new DataBox(data.DataPointer, 0, 0), buffer);
            }
        }

        public void SetupStructuredBuffer(int sizeInBytes, int stride, ref Buffer buffer)
        {
            if (buffer == null || buffer.Description.SizeInBytes != sizeInBytes)
            {
                buffer?.Dispose();
                var bufferDesc = new BufferDescription
                                 {
                                     Usage = ResourceUsage.Default,
                                     BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                                     SizeInBytes = sizeInBytes,
                                     OptionFlags = ResourceOptionFlags.BufferStructured,
                                     StructureByteStride = stride
                                 };
                buffer = new Buffer(Device, bufferDesc);
            }
        }

        public void CreateStructuredBufferSrv(Buffer buffer, ref ShaderResourceView srv)
        {
            if (buffer == null)
                return;

            if ((buffer.Description.OptionFlags & ResourceOptionFlags.BufferStructured) == 0)
            {
                // Log.Warning($"{nameof(SrvFromStructuredBuffer)} - input buffer is not structured, skipping SRV creation.");
                return;
            }

            srv?.Dispose();
            var srvDesc = new ShaderResourceViewDescription()
                          {
                              Dimension = ShaderResourceViewDimension.ExtendedBuffer,
                              Format = Format.Unknown,
                              BufferEx = new ShaderResourceViewDescription.ExtendedBufferResource
                                         {
                                             FirstElement = 0,
                                             ElementCount = buffer.Description.SizeInBytes / buffer.Description.StructureByteStride
                                         }
                          };
            srv = new ShaderResourceView(Device, buffer, srvDesc);
        }

        public void CreateStructuredBufferUav(Buffer buffer, UnorderedAccessViewBufferFlags bufferFlags, ref UnorderedAccessView uav)
        {
            if (buffer == null)
                return;

            if ((buffer.Description.OptionFlags & ResourceOptionFlags.BufferStructured) == 0)
            {
                // Log.Warning($"{nameof(SrvFromStructuredBuffer)} - input buffer is not structured, skipping SRV creation.");
                return;
            }

            uav?.Dispose();
            var uavDesc = new UnorderedAccessViewDescription()
                          {
                              Dimension = UnorderedAccessViewDimension.Buffer,
                              Format = Format.Unknown,
                              Buffer = new UnorderedAccessViewDescription.BufferResource
                                       {
                                           FirstElement = 0,
                                           ElementCount = buffer.Description.SizeInBytes / buffer.Description.StructureByteStride,
                                           Flags = bufferFlags
                                       }
                          };
            uav = new UnorderedAccessView(Device, buffer, uavDesc);
        }

        class IncludeHandler : SharpDX.D3DCompiler.Include
        {
            private StreamReader _streamReader;

            public void Dispose()
            {
                _streamReader?.Dispose();
            }

            public IDisposable Shadow { get; set; }

            public Stream Open(IncludeType type, string fileName, Stream parentStream)
            {
                try
                {
                    _streamReader = new StreamReader(Path.Combine(ResourcesFolder, fileName));
                }
                catch(DirectoryNotFoundException e )
                {
                    Log.Error($"Can't open file {ResourcesFolder}/{fileName}  {e.Message}");
                    return null;
                }
                return _streamReader.BaseStream;
            }

            public void Close(Stream stream)
            {
                _streamReader.Close();
            }
        }

        internal void CompileShader<TShader>(string srcFile, string entryPoint, string name, string profile, ref TShader shader, ref ShaderBytecode blob)
            where TShader : class, IDisposable
        {
            CompilationResult compilationResult = null;
            try
            {
                ShaderFlags flags = ShaderFlags.None;
                #if DEBUG || FORCE_SHADER_DEBUG
                flags |= ShaderFlags.Debug;
                #endif
                compilationResult = ShaderBytecode.CompileFromFile(srcFile, entryPoint, profile, flags, EffectFlags.None, null, new IncludeHandler());
            }
            catch (Exception ce)
            {
                Log.Error($"Failed to compile shader '{name}': {ce.Message}\nUsing previous resource state.");
                return;
            }

            blob?.Dispose();
            blob = compilationResult.Bytecode;

            shader?.Dispose();
            // as shader type is generic we've to use Activator and PropertyInfo to create/set the shader object
            Type shaderType = typeof(TShader);
            shader = (TShader)Activator.CreateInstance(shaderType, Device, blob.Data, null);
            PropertyInfo debugNameInfo = shaderType.GetProperty("DebugName");
            debugNameInfo?.SetValue(shader, name);

            Log.Info($"Successfully compiled shader '{name}' with profile '{profile}' from '{srcFile}'");
        }

        public uint CreateVertexShaderFromFile(string srcFile, string entryPoint, string name, Action fileChangedAction)
        {
            if (string.IsNullOrEmpty(srcFile) || string.IsNullOrEmpty(entryPoint))
                return NullResource;

            bool foundFileEntryForPath = _fileResources.TryGetValue(srcFile, out var fileResource);
            if (foundFileEntryForPath)
            {
                foreach (var id in fileResource.ResourceIds)
                {
                    if (Resources[id] is VertexShaderResource)
                    {
                        fileResource.FileChangeAction -= fileChangedAction;
                        fileResource.FileChangeAction += fileChangedAction;
                        return id;
                    }
                }
            }

            VertexShader vertexShader = null;
            ShaderBytecode blob = null;
            CompileShader(srcFile, entryPoint, name, "vs_5_0", ref vertexShader, ref blob);
            if (vertexShader == null)
            {
                Log.Info($"Failed to create vertex shader '{name}'.");
                return NullResource;
            }

            var resourceEntry = new VertexShaderResource(GetNextResourceId(), name, entryPoint, blob, vertexShader);
            Resources.Add(resourceEntry.Id, resourceEntry);
            _vertexShaders.Add(resourceEntry);
            if (fileResource == null)
            {
                fileResource = new FileResource(srcFile, new[] { resourceEntry.Id });
                _fileResources.Add(srcFile, fileResource);
            }
            else
            {
                // file resource already exists, so just add the id of the new type resource
                fileResource.ResourceIds.Add(resourceEntry.Id);
            }

            fileResource.FileChangeAction -= fileChangedAction;
            fileResource.FileChangeAction += fileChangedAction;

            return resourceEntry.Id;
        }

        public uint CreatePixelShaderFromFile(string srcFile, string entryPoint, string name, Action fileChangedAction)
        {
            if (string.IsNullOrEmpty(srcFile) || string.IsNullOrEmpty(entryPoint))
                return NullResource;

            bool foundFileEntryForPath = _fileResources.TryGetValue(srcFile, out var fileResource);
            if (foundFileEntryForPath)
            {
                foreach (var id in fileResource.ResourceIds)
                {
                    if (Resources[id] is PixelShaderResource)
                    {
                        fileResource.FileChangeAction -= fileChangedAction;
                        fileResource.FileChangeAction += fileChangedAction;
                        return id;
                    }
                }
            }

            PixelShader shader = null;
            ShaderBytecode blob = null;
            CompileShader(srcFile, entryPoint, name, "ps_5_0", ref shader, ref blob);
            if (shader == null)
            {
                Log.Info($"Failed to create pixel shader '{name}'.");
                return NullResource;
            }

            var resourceEntry = new PixelShaderResource(GetNextResourceId(), name, entryPoint, blob, shader);
            Resources.Add(resourceEntry.Id, resourceEntry);
            _pixelShaders.Add(resourceEntry);
            if (fileResource == null)
            {
                fileResource = new FileResource(srcFile, new[] { resourceEntry.Id });
                _fileResources.Add(srcFile, fileResource);
            }
            else
            {
                // file resource already exists, so just add the id of the new type resource
                fileResource.ResourceIds.Add(resourceEntry.Id);
            }

            fileResource.FileChangeAction -= fileChangedAction;
            fileResource.FileChangeAction += fileChangedAction;

            return resourceEntry.Id;
        }

        public uint CreateComputeShaderFromFile(string srcFile, string entryPoint, string name, Action fileChangedAction)
        {
            if (string.IsNullOrEmpty(srcFile) || string.IsNullOrEmpty(entryPoint))
                return NullResource;

            bool foundFileEntryForPath = _fileResources.TryGetValue(srcFile, out var fileResource);
            if (foundFileEntryForPath)
            {
                foreach (var id in fileResource.ResourceIds)
                {
                    if (Resources[id] is ComputeShaderResource csResource)
                    {
                        if (csResource.EntryPoint == entryPoint)
                        {
                            fileResource.FileChangeAction -= fileChangedAction;
                            fileResource.FileChangeAction += fileChangedAction;
                            return id;
                        }
                    }
                }
            }

            ComputeShader shader = null;
            ShaderBytecode blob = null;
            CompileShader(srcFile, entryPoint, name, "cs_5_0", ref shader, ref blob);
            if (shader == null)
            {
                Log.Info($"Failed to create compute shader '{name}'.");
                return NullResource;
            }

            var resourceEntry = new ComputeShaderResource(GetNextResourceId(), name, entryPoint, blob, shader);
            Resources.Add(resourceEntry.Id, resourceEntry);
            _computeShaders.Add(resourceEntry);
            if (fileResource == null)
            {
                fileResource = new FileResource(srcFile, new[] { resourceEntry.Id });
                _fileResources.Add(srcFile, fileResource);
            }
            else
            {
                // file resource already exists, so just add the id of the new type resource
                fileResource.ResourceIds.Add(resourceEntry.Id);
            }

            fileResource.FileChangeAction -= fileChangedAction;
            fileResource.FileChangeAction += fileChangedAction;

            return resourceEntry.Id;
        }
        
        
        public uint CreateGeometryShaderFromFile(string srcFile, string entryPoint, string name, Action fileChangedAction)
        {
            if (string.IsNullOrEmpty(srcFile) || string.IsNullOrEmpty(entryPoint))
                return NullResource;

            bool foundFileEntryForPath = _fileResources.TryGetValue(srcFile, out var fileResource);
            if (foundFileEntryForPath)
            {
                foreach (var id in fileResource.ResourceIds)
                {
                    if (Resources[id] is GeometryShaderResource gsResource)
                    {
                        if (gsResource.EntryPoint == entryPoint)
                        {
                            fileResource.FileChangeAction -= fileChangedAction;
                            fileResource.FileChangeAction += fileChangedAction;
                            return id;
                        }
                    }
                }
            }

            GeometryShader shader = null;
            ShaderBytecode blob = null;
            CompileShader(srcFile, entryPoint, name, "gs_5_0", ref shader, ref blob);
            if (shader == null)
            {
                Log.Info($"Failed to create geometry shader '{name}'.");
                return NullResource;
            }

            var resourceEntry = new GeometryShaderResource(GetNextResourceId(), name, entryPoint, blob, shader);
            Resources.Add(resourceEntry.Id, resourceEntry);
            _geometryShaders.Add(resourceEntry);
            if (fileResource == null)
            {
                fileResource = new FileResource(srcFile, new[] { resourceEntry.Id });
                _fileResources.Add(srcFile, fileResource);
            }
            else
            {
                // file resource already exists, so just add the id of the new type resource
                fileResource.ResourceIds.Add(resourceEntry.Id);
            }

            fileResource.FileChangeAction -= fileChangedAction;
            fileResource.FileChangeAction += fileChangedAction;

            return resourceEntry.Id;
        }

        public void UpdateVertexShaderFromFile(string path, uint id, ref VertexShader vertexShader)
        {
            Resources.TryGetValue(id, out var resource);
            if (resource is VertexShaderResource vsResource)
            {
                vsResource.Update(path);
                vertexShader = vsResource.VertexShader;
            }
        }

        public void UpdatePixelShaderFromFile(string path, uint id, ref PixelShader vertexShader)
        {
            Resources.TryGetValue(id, out var resource);
            if (resource is PixelShaderResource vsResource)
            {
                vsResource.Update(path);
                vertexShader = vsResource.PixelShader;
            }
        }

        public void UpdateComputeShaderFromFile(string path, uint id, ref ComputeShader computeShader)
        {
            Resources.TryGetValue(id, out var resource);
            if (resource is ComputeShaderResource csResource)
            {
                csResource.Update(path);
                computeShader = csResource.ComputeShader;
            }
        }

        public void UpdateGeometryShaderFromFile(string path, uint id, ref GeometryShader geometryShader)
        {
            Resources.TryGetValue(id, out var resource);
            if (resource is GeometryShaderResource gsResource)
            {
                gsResource.Update(path);
                geometryShader = gsResource.GeometryShader;
            }
        }
        
        public uint CreateOperatorEntry(string sourceFilePath, string name, OperatorResource.UpdateDelegate updateHandler)
        {
            // todo: code below is redundant with all file resources -> refactor
            if (_fileResources.TryGetValue(sourceFilePath, out var fileResource))
            {
                foreach (var id in fileResource.ResourceIds)
                {
                    if (Resources[id] is OperatorResource)
                    {
                        return id;
                    }
                }
            }

            var resourceEntry = new OperatorResource(GetNextResourceId(), name, null, updateHandler);
            Resources.Add(resourceEntry.Id, resourceEntry);
            
            if (fileResource == null)
            {
                fileResource = new FileResource(sourceFilePath, new[] { resourceEntry.Id });
                _fileResources.Add(sourceFilePath, fileResource);
            }
            else
            {
                // File resource already exists, so just add the id of the new type resource
                fileResource.ResourceIds.Add(resourceEntry.Id);
            }

            return resourceEntry.Id;
        }



        private void OnChanged(object sender, FileSystemEventArgs fileSystemEventArgs)
        {
            // Log.Info($"change for '{fileSystemEventArgs.Name}' due to '{fileSystemEventArgs.ChangeType}'.");
            if (!_fileResources.TryGetValue(fileSystemEventArgs.FullPath, out var fileResource))
            {
                //Log.Warning("Invalid FileResource?");
                return;
            }

            // Log.Info($"valid change for '{fileSystemEventArgs.Name}' due to '{fileSystemEventArgs.ChangeType}'.");
            DateTime lastWriteTime = File.GetLastWriteTime(fileSystemEventArgs.FullPath);
            if (lastWriteTime != fileResource.LastWriteReferenceTime)
            {
                // Log.Info($"very valid change for '{fileSystemEventArgs.Name}' due to '{fileSystemEventArgs.ChangeType}'.");
                // hack: in order to prevent editors like vs-code still having the file locked after writing to it, this gives these editors 
                //       some time to release the lock. With a locked file Shader.ReadFromFile(...) function will throw an exception, because
                //       it cannot read the file. 
                Thread.Sleep(15);
                Log.Info($"File '{fileSystemEventArgs.FullPath}' changed due to {fileSystemEventArgs.ChangeType}");
                foreach (var id in fileResource.ResourceIds)
                {
                    // update all resources that depend from this file
                    if (Resources.TryGetValue(id, out var resource))
                    {
                        var updateable = resource as IUpdateable;
                        updateable?.Update(fileResource.Path);
                        resource.UpToDate = false;
                    }
                    else
                    {
                        Log.Info($"Trying to update a non existing file resource '{fileResource.Path}'.");
                    }
                }

                fileResource.FileChangeAction?.Invoke();

                fileResource.LastWriteReferenceTime = lastWriteTime;
            }

            // else discard the (duplicated) OnChanged event
        }

        private void OnRenamed(object sender, RenamedEventArgs renamedEventArgs)
        {
            RenameOperatorResource(renamedEventArgs.OldFullPath, renamedEventArgs.FullPath);
        }

        public static Texture2D CreateTexture2DFromBitmap(Device device, BitmapSource bitmapSource)
        {
            // Allocate DataStream to receive the WIC image pixels
            int stride = bitmapSource.Size.Width * 4;
            using (var buffer = new SharpDX.DataStream(bitmapSource.Size.Height * stride, true, true))
            {
                // Copy the content of the WIC to the buffer
                bitmapSource.CopyPixels(stride, buffer);
                int mipLevels = (int)Math.Log(bitmapSource.Size.Width, 2.0) + 1;
                var texDesc = new Texture2DDescription()
                              {
                                  Width = bitmapSource.Size.Width,
                                  Height = bitmapSource.Size.Height,
                                  ArraySize = 1,
                                  BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                                  Usage = ResourceUsage.Default,
                                  CpuAccessFlags = CpuAccessFlags.None,
                                  Format = SharpDX.DXGI.Format.R8G8B8A8_UNorm,
                                  MipLevels = mipLevels,
                                  OptionFlags = ResourceOptionFlags.GenerateMipMaps,
                                  SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                              };
                var dataRectangles = new DataRectangle[mipLevels];
                for (int i = 0; i < mipLevels; i++)
                {
                    dataRectangles[i] = new DataRectangle(buffer.DataPointer, stride);
                    stride /= 2;
                }

                return new Texture2D(device, texDesc, dataRectangles);
            }
        }

        public void CreateTexture2d(string filename, ref Texture2D texture)
        {
            try
            {
                ImagingFactory factory = new ImagingFactory();
                var bitmapDecoder = new BitmapDecoder(factory, filename, DecodeOptions.CacheOnDemand);
                var formatConverter = new FormatConverter(factory);
                var bitmapFrameDecode = bitmapDecoder.GetFrame(0);
                formatConverter.Initialize(bitmapFrameDecode, PixelFormat.Format32bppPRGBA, BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);

                texture?.Dispose();
                texture = CreateTexture2DFromBitmap(Device, formatConverter);
                string name = Path.GetFileName(filename);
                texture.DebugName = name;
                bitmapFrameDecode.Dispose();
                bitmapDecoder.Dispose();
                formatConverter.Dispose();
                factory.Dispose();
                Log.Info($"Created texture '{name}' from '{filename}'");
            }
            catch (Exception e)
            {
                Log.Info($"Info: couldn't access file '{filename}': {e.Message}.");
            }
        }

        public uint GetIdForTexture(Texture2D texture)
        {
            if (texture == null)
                return NullResource;

            foreach (var (id, resourceEntry) in Resources)
            {
                if (resourceEntry is Texture2dResource textureResource)
                {
                    if (textureResource.Texture == texture)
                        return id;
                }
            }

            return NullResource;
        }
        
        public uint GetIdForTexture(Texture3D texture)
        {
            if (texture == null)
                return NullResource;

            foreach (var (id, resourceEntry) in Resources)
            {
                if (resourceEntry is Texture3dResource textureResource)
                {
                    if (textureResource.Texture == texture)
                        return id;
                }
            }

            return NullResource;
        }

        public void CreateShaderResourceView(uint textureId, string name, ref ShaderResourceView shaderResourceView)
        {
            if (Resources.TryGetValue(textureId, out var resource))
            {
                if (resource is Texture2dResource texture2dResource)
                {
                    shaderResourceView?.Dispose();
                    shaderResourceView = new ShaderResourceView(Device, texture2dResource.Texture) { DebugName = name };
                    Log.Info($"Created shader resource view '{name}' for texture '{texture2dResource.Name}'.");
                }
                else if (resource is Texture3dResource texture3dResource)
                {
                    shaderResourceView?.Dispose();
                    shaderResourceView = new ShaderResourceView(Device, texture3dResource.Texture) { DebugName = name };
                    Log.Info($"Created shader resource view '{name}' for texture '{texture3dResource.Name}'.");
                }
                else
                {
                    Log.Error("Trying to generate shader resource view from a resource that's not a texture resource");
                }
            }
            else
            {
                Log.Error($"Trying to look up texture resource with id {textureId} but did not found it.");
            }
        }
        
        
        public void CreateUnorderedAccessView(uint textureId, string name, ref UnorderedAccessView unorderedAccessView)
        {
            if (Resources.TryGetValue(textureId, out var resource))
            {
                if (resource is Texture2dResource texture2dResource)
                {
                    unorderedAccessView?.Dispose();
                    unorderedAccessView = new UnorderedAccessView(Device, texture2dResource.Texture) { DebugName = name };
                    Log.Info($"Created unordered resource view '{name}' for texture '{texture2dResource.Name}'.");
                }
                else if (resource is Texture3dResource texture3dResource)
                {
                    unorderedAccessView?.Dispose();
                    unorderedAccessView = new UnorderedAccessView(Device, texture3dResource.Texture) { DebugName = name };
                    Log.Info($"Created unordered resource view '{name}' for texture '{texture3dResource.Name}'.");
                }
                else
                {
                    Log.Error("Trying to generate unordered resource view from a resource that's not a texture resource");
                }
            }
            else
            {
                Log.Error($"Trying to look up texture resource with id {textureId} but did not found it.");
            }
        }
        
        public void CreateRenderTargetView(uint textureId, string name, ref RenderTargetView renderTargetView)
        {
            if (Resources.TryGetValue(textureId, out var resource))
            {
                if (resource is Texture2dResource texture2dResource)
                {
                    renderTargetView?.Dispose();
                    renderTargetView = new RenderTargetView(Device, texture2dResource.Texture) { DebugName = name };
                    Log.Info($"Created render target view '{name}' for texture '{texture2dResource.Name}'.");
                }
                else if (resource is Texture3dResource texture3dResource)
                {
                    renderTargetView?.Dispose();
                    renderTargetView = new RenderTargetView(Device, texture3dResource.Texture) { DebugName = name };
                    Log.Info($"Created render target view '{name}' for texture '{texture3dResource.Name}'.");
                }
                else
                {
                    Log.Error("Trying to generate render target view from a resource that's not a texture resource");
                }
            }
            else
            {
                Log.Error($"Trying to look up texture resource with id {textureId} but did not found it.");
            }
        }

        /**
         * Returns a textureViewResourceEntryId
         */
        public uint CreateShaderResourceView(uint textureId, string name)
        {
            ShaderResourceView textureView = null;
            CreateShaderResourceView(textureId, name, ref textureView);
            var textureViewResourceEntry = new ShaderResourceViewResource(GetNextResourceId(), name, textureView, textureId);
            Resources.Add(textureViewResourceEntry.Id, textureViewResourceEntry);
            _shaderResourceViews.Add(textureViewResourceEntry);
            return textureViewResourceEntry.Id;
        }

        /* TODO, ResourceUsage usage, BindFlags bindFlags, CpuAccessFlags cpuAccessFlags, ResourceOptionFlags miscFlags, int loadFlags*/
        public (uint textureId, uint srvResourceId) CreateTextureFromFile(string filename, Action fileChangeAction)
        {
            if (!File.Exists(filename))
            {
                Log.Warning($"Couldn't find texture '{filename}'.");
                return (NullResource, NullResource);
            }

            if (_fileResources.TryGetValue(filename, out var existingFileResource))
            {
                uint textureId = existingFileResource.ResourceIds.First();
                existingFileResource.FileChangeAction += fileChangeAction;
                uint srvId = (from srvResourceEntry in _shaderResourceViews
                              where srvResourceEntry.TextureId == textureId
                              select srvResourceEntry.Id).Single();
                return (textureId, srvId);
            }

            Texture2D texture = null;
            ShaderResourceView srv = null;
            uint srvResourceId = NullResource;
            if (filename.ToLower().EndsWith(".dds"))
            {
                DdsImport.CreateDdsTextureFromFile(Device.NativePointer, Device.ImmediateContext.NativePointer, filename,
                                                   out IntPtr texPtr, out IntPtr srvPtr);

                texture = new Texture2D(texPtr);
                srv = new ShaderResourceView(srvPtr);
            } 
            else
            {
                CreateTexture2d(filename, ref texture);
            }

            string name = Path.GetFileName(filename);
            var textureResourceEntry = new Texture2dResource(GetNextResourceId(), name, texture);
            Resources.Add(textureResourceEntry.Id, textureResourceEntry);
            _2dTextures.Add(textureResourceEntry);

            if (srv == null)
            {
                srvResourceId = CreateShaderResourceView(textureResourceEntry.Id, name);
            } 
            else
            {
                var textureViewResourceEntry = new ShaderResourceViewResource(GetNextResourceId(), name, srv, textureResourceEntry.Id);
                Resources.Add(textureViewResourceEntry.Id, textureViewResourceEntry);
                _shaderResourceViews.Add(textureViewResourceEntry);
                srvResourceId = textureViewResourceEntry.Id;
            }

            var fileResource = new FileResource(filename, new[] { textureResourceEntry.Id, srvResourceId });
            fileResource.FileChangeAction += fileChangeAction;
            _fileResources.Add(filename, fileResource);

            return (textureResourceEntry.Id, srvResourceId);
        }

        public void UpdateTextureFromFile(uint textureId, string path, ref Texture2D texture)
        {
            Resources.TryGetValue(textureId, out var resource);
            if (resource is Texture2dResource textureResource)
            {
                CreateTexture2d(path, ref textureResource.Texture);
                texture = textureResource.Texture;
            }
        }

        // returns true if the texture changed
        public bool CreateTexture2d(Texture2DDescription description, string name, ref uint id, ref Texture2D texture)
        {
            if (texture != null)
            {
                bool descriptionMatches;
                try
                {
                    descriptionMatches = texture.Description.Equals(description);
                }
                catch
                {
                    descriptionMatches = false;
                }

                if (descriptionMatches)
                {
                    return false; // no change
                }
            }

            Resources.TryGetValue(id, out var resource);
            Texture2dResource texture2dResource = resource as Texture2dResource;

            if (texture2dResource == null)
            {
                // no entry so far, if texture is also null then create a new one
                if (texture == null)
                {
                    texture = new Texture2D(Device, description);
                }

                // new texture, create resource entry
                texture2dResource = new Texture2dResource(GetNextResourceId(), name, texture);
                Resources.Add(texture2dResource.Id, texture2dResource);
                _2dTextures.Add(texture2dResource);
            }
            else
            {
                texture2dResource.Texture?.Dispose();
                texture2dResource.Texture = new Texture2D(Device, description);
                texture = texture2dResource.Texture;
            }

            id = texture2dResource.Id;

            return true;
        }
        
        public bool CreateTexture3d(Texture3DDescription description, string name, ref uint id, ref Texture3D texture)
        {
            if (texture != null && texture.Description.Equals(description))
            {
                return false; // no change
            }
        
            Resources.TryGetValue(id, out var resource);
            Texture3dResource texture3dResource = resource as Texture3dResource;
        
            if (texture3dResource == null)
            {
                // no entry so far, if texture is also null then create a new one
                if (texture == null)
                {
                    texture = new Texture3D(Device, description);
                }
        
                // new texture, create resource entry
                texture3dResource = new Texture3dResource(GetNextResourceId(), name, texture);
                Resources.Add(texture3dResource.Id, texture3dResource);
                _3dTextures.Add(texture3dResource);
            }
            else
            {
                texture3dResource.Texture?.Dispose();
                texture3dResource.Texture = new Texture3D(Device, description);
                texture = texture3dResource.Texture;
            }
        
            id = texture3dResource.Id;
        
            return true;
        }


        public readonly Dictionary<uint, Resource> Resources = new Dictionary<uint, Resource>();

        /// <summary>Maps full filepath to FileResource</summary>
        private readonly Dictionary<string, FileResource> _fileResources = new Dictionary<string, FileResource>();

        private readonly List<VertexShaderResource> _vertexShaders = new List<VertexShaderResource>();
        private readonly List<PixelShaderResource> _pixelShaders = new List<PixelShaderResource>();
        private readonly List<ComputeShaderResource> _computeShaders = new List<ComputeShaderResource>();
        private readonly List<GeometryShaderResource> _geometryShaders = new List<GeometryShaderResource>();
        private readonly List<Texture2dResource> _2dTextures = new List<Texture2dResource>();
        private readonly List<Texture3dResource> _3dTextures = new List<Texture3dResource>();
        private readonly List<ShaderResourceViewResource> _shaderResourceViews = new List<ShaderResourceViewResource>();
        
        private readonly FileSystemWatcher _hlslFileWatcher;
        private readonly FileSystemWatcher _pngFileWatcher;
        private readonly FileSystemWatcher _jpgFileWatcher;
        private readonly FileSystemWatcher _ddsFileWatcher;
        private readonly FileSystemWatcher _tiffFileWatcher;
        private readonly FileSystemWatcher _csFileWatcher;

        public readonly Device Device;
        public const string ResourcesFolder = @"Resources";
    }
}