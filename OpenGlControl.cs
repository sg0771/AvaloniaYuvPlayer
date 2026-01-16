using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using System;
using System.IO;

namespace AvaloniaYuvPlayer;

public class AvaloniaBindingsContext : IBindingsContext
{
    private readonly GlInterface _gl;

    public AvaloniaBindingsContext(GlInterface gl)
    {
        _gl = gl;
    }

    public IntPtr GetProcAddress(string procName)
    {
        return _gl.GetProcAddress(procName);
    }
}

public class OpenGlControl : OpenGlControlBase
{

    int _program, _texY, _texU, _texV;
    int _vao, _vbo;
    int _w = 352, _h = 288;
    FileStream? _fs;
    DispatcherTimer? _timer;

    int _posLoc, _uvLoc;

    public int WXCreateProgram()
    {
        string vs = """
        #version 100
        attribute vec2 aPos;
        attribute vec2 aUV;
        varying vec2 vUV;

        void main()
        {
            gl_Position = vec4(aPos, 0.0, 1.0);
            vUV = aUV;
        }
        """;

        string fs = """
        #version 100
        precision mediump float;

        varying vec2 vUV;
        uniform sampler2D texY;
        uniform sampler2D texU;
        uniform sampler2D texV;

        void main()
        {
            float y = texture2D(texY, vUV).r;
            float u = texture2D(texU, vUV).r - 0.5;
            float v = texture2D(texV, vUV).r - 0.5;

            float r = y + 1.402 * v;
            float g = y - 0.344 * u - 0.714 * v;
            float b = y + 1.772 * u;

            gl_FragColor = vec4(r, g, b, 1.0);
        }
        """;

        int v = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(v, vs);
        GL.CompileShader(v);
        string vs_info = GL.GetShaderInfoLog(v);
        if (!string.IsNullOrEmpty(vs_info))
            Console.WriteLine("VS Info: " + vs_info);

        int f = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(f, fs);
        GL.CompileShader(f);
        string fs_info = GL.GetShaderInfoLog(f);
        if (!string.IsNullOrEmpty(fs_info))
            Console.WriteLine("FS Info: " + fs_info);

        int p = GL.CreateProgram();
        GL.AttachShader(p, v);
        GL.AttachShader(p, f);
        GL.LinkProgram(p);

        string p_info = GL.GetProgramInfoLog(p);
        if (!string.IsNullOrEmpty(p_info))
            Console.WriteLine("Program Info: " + p_info);

        // 检查链接状态
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            Console.WriteLine("Program link failed!");
            return 0;
        }

        // 获取属性位置，但不在这里设置顶点属性指针
        _posLoc = GL.GetAttribLocation(p, "aPos");
        _uvLoc = GL.GetAttribLocation(p, "aUV");

        Console.WriteLine($"Attribute locations: aPos={_posLoc}, aUV={_uvLoc}");

        return p;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        // ✅ 关键：把 Avalonia 的 OpenGL 函数地址绑定给 OpenTK
        OpenTK.Graphics.OpenGL4.GL.LoadBindings(new AvaloniaBindingsContext(gl));


        string strGL = OpenTK.Graphics.OpenGL4.GL.GetString(
            OpenTK.Graphics.OpenGL4.StringName.Version);
        Console.WriteLine("OpenGL = " + strGL);
        string strGLSL = OpenTK.Graphics.OpenGL4.GL.GetString(
            OpenTK.Graphics.OpenGL4.StringName.ShadingLanguageVersion);
        Console.WriteLine("GLSL = " + strGLSL);

        _program = WXCreateProgram();

        if (_program == 0)
        {
            Console.WriteLine("Failed to create shader program!");
            return;
        }

        // 创建顶点数据
        float[] vertices =
        {
            // pos      // uv
            -1f, -1f,   0f, 1f,
             1f, -1f,   1f, 1f,
             1f,  1f,   1f, 0f,

            -1f, -1f,   0f, 1f,
             1f,  1f,   1f, 0f,
            -1f,  1f,   0f, 0f,
        };

        // 创建并绑定VAO
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        // 创建并绑定VBO
        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,
            vertices.Length * sizeof(float),
            vertices,
            BufferUsageHint.StaticDraw);

        // 设置顶点属性 - 必须在VAO绑定状态下进行
        GL.EnableVertexAttribArray(_posLoc);
        GL.VertexAttribPointer(_posLoc, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);

        GL.EnableVertexAttribArray(_uvLoc);
        GL.VertexAttribPointer(_uvLoc, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));

        // 解绑VAO和VBO
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        // 创建纹理
        _texY = GL.GenTexture();
        _texU = GL.GenTexture();
        _texV = GL.GenTexture();

        // 初始化纹理
        InitializeTexture(_texY, _w, _h);
        InitializeTexture(_texU, _w / 2, _h / 2);
        InitializeTexture(_texV, _w / 2, _h / 2);

        // 设置统一变量
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "texY"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "texU"), 1);
        GL.Uniform1(GL.GetUniformLocation(_program, "texV"), 2);
        GL.UseProgram(0);

        GL.ClearColor(0, 0, 0, 1); // 改为黑色背景，更容易看到问题
    }

    void InitializeTexture(int tex, int w, int h)
    {
        GL.BindTexture(TextureTarget.Texture2D, tex);
        // 上传空纹理数据（初始化为黑色）
        GL.TexImage2D(TextureTarget.Texture2D, 0,
            PixelInternalFormat.R8, w, h, 0,
            PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);

        // 关键修复：添加Mipmap配置（必须，否则部分GPU不工作）
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        // 生成Mipmap（纹理完整初始化的必要步骤）
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Start(string file, int w, int h, int fps)
    {
        Stop();

        _w = w; _h = h;
        _fs = File.OpenRead(file);

        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        _timer.Tick += (_, _) => ReadFrame();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _fs?.Close();
        _fs = null;
    }

    void ReadFrame()
    {
        if (_fs == null) return;

        int ySize = _w * _h;
        int uvSize = ySize / 4;

        byte[] y = new byte[ySize];
        byte[] u = new byte[uvSize];
        byte[] v = new byte[uvSize];

        if (_fs.Read(y) < ySize)
        {
            Stop();
            return;
        }
        _fs.Read(u);
        _fs.Read(v);

        Upload(_texY, _w, _h, y);
        Upload(_texU, _w / 2, _h / 2, u);
        Upload(_texV, _w / 2, _h / 2, v);

        // 在 ReadFrame() 最后添加
        Dispatcher.UIThread.Post(RequestNextFrameRendering);
        Console.WriteLine("Read frame at position: " + _fs.Position);
    }

    void Upload(int tex, int w, int h, byte[] data)
    {
        if (data == null || data.Length == 0)
            return;
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1); // 必须（YUV数据无4字节对齐）
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h,
            PixelFormat.Red, PixelType.UnsignedByte, data);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D); // 修复：上传数据后更新Mipmap
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_program == 0) return;

        GL.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);

        // 绑定纹理到正确的纹理单元
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _texY);

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _texU);

        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _texV);

        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // 清理状态
        GL.BindVertexArray(0);
        GL.UseProgram(0);

        // 重置纹理单元
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Stop();

        if (_program != 0)
        {
            GL.DeleteProgram(_program);
            _program = 0;
        }

        if (_vao != 0)
        {
            GL.DeleteVertexArray(_vao);
            _vao = 0;
        }

        if (_vbo != 0)
        {
            GL.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_texY != 0) GL.DeleteTexture(_texY);
        if (_texU != 0) GL.DeleteTexture(_texU);
        if (_texV != 0) GL.DeleteTexture(_texV);
    }
}