/*
 
    <PackageReference Include="Avalonia" Version="11.0.7" />
    <PackageReference Include="Avalonia.Desktop" Version="11.0.7" />
	  <PackageReference Include="Avalonia.ReactiveUI" Version="11.0.7" />
	  <PackageReference Include="Avalonia.Themes.Fluent" Version="11.0.7" />
    <PackageReference Include="OpenTK" Version="4.8.0" />

 */

using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK;
using OpenTK.Compute.OpenCL;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
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
    int m_ySize = 0;
    int m_uvSize = 0;
    private byte[] m_pY, m_pU, m_pV;
    private readonly object _lock = new();

    int m_program, m_texY, m_texU, m_texV;
    int m_vao, m_vbo;
    int m_w = 352, m_h = 288;
    FileStream? _fs;
    DispatcherTimer? _timer;

    int m_posLoc, m_uvLoc;

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
        m_posLoc = GL.GetAttribLocation(p, "aPos");
        m_uvLoc = GL.GetAttribLocation(p, "aUV");

        Console.WriteLine($"Attribute locations: aPos={m_posLoc}, aUV={m_uvLoc}");

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

        m_program = WXCreateProgram();

        if (m_program == 0)
        {
            Console.WriteLine("Failed to create shader program!");
            return;
        }

        // 创建顶点数据
        float[] m_vertices =
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
        m_vao = GL.GenVertexArray();
        GL.BindVertexArray(m_vao);

        // 创建并绑定VBO
        m_vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, m_vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,
            m_vertices.Length * sizeof(float),
            m_vertices,
            BufferUsageHint.StaticDraw);

        // 设置顶点属性 - 必须在VAO绑定状态下进行
        GL.EnableVertexAttribArray(m_posLoc);
        GL.VertexAttribPointer(m_posLoc, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);

        GL.EnableVertexAttribArray(m_uvLoc);
        GL.VertexAttribPointer(m_uvLoc, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));

        // 解绑VAO和VBO
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        // 创建纹理
        m_texY = GL.GenTexture();
        m_texU = GL.GenTexture();
        m_texV = GL.GenTexture();

        // 初始化纹理
        InitializeTexture(m_texY, m_w, m_h);
        InitializeTexture(m_texU, m_w / 2, m_h / 2);
        InitializeTexture(m_texV, m_w / 2, m_h / 2);

        // 设置统一变量
        // 在 OnOpenGlInit 的末尾
        GL.UseProgram(m_program);

        int locY = GL.GetUniformLocation(m_program, "texY");
        int locU = GL.GetUniformLocation(m_program, "texU");
        int locV = GL.GetUniformLocation(m_program, "texV");

        Console.WriteLine($"Uniform locations: Y={locY}, U={locU}, V={locV}");

        GL.Uniform1(locY, 0); // 对应 TextureUnit.Texture0
        GL.Uniform1(locU, 1); // 对应 TextureUnit.Texture1
        GL.Uniform1(locV, 2); // 对应 TextureUnit.Texture2

        GL.UseProgram(0);

        GL.ClearColor(0, 0, 0, 1); // 改为黑色背景，更容易看到问题
    }

    void InitializeTexture(int tex, int w, int h)
    {
        GL.BindTexture(TextureTarget.Texture2D, tex);

        // 统一使用 Luminance 格式
        GL.TexImage2D(TextureTarget.Texture2D, 0,
            PixelInternalFormat.Luminance, w, h, 0,
            PixelFormat.Luminance, PixelType.UnsignedByte, IntPtr.Zero);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Start(string file, int w, int h, int fps)
    {
        Stop();

        m_w = w; m_h = h;
        _fs = File.OpenRead(file);

        m_ySize = m_w * m_h;
        m_uvSize = m_ySize / 4;
        m_pY = new byte[m_ySize];
        m_pU = new byte[m_uvSize];
        m_pV = new byte[m_uvSize];


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

        // ... 在 ReadFrame 结尾替换 Upload 调用
        lock (_lock)
        {
            int readY = _fs.Read(m_pY, 0, m_pY.Length);

            if (readY < m_pY.Length)
            {
                return;
            }

            int readU = _fs.Read(m_pU, 0, m_pU.Length);
            int readV = _fs.Read(m_pV, 0, m_pV.Length);
        }
        RequestNextFrameRendering();
    }

    void Upload(int tex, int w, int h, byte[] data)
    {
        if (data == null || data.Length == 0) return;

        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        // ✅ 必须锁定内存并传递指针，确保数据传到显卡
        unsafe
        {
            fixed (byte* p = data)
            {
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h,
                    PixelFormat.Luminance, PixelType.UnsignedByte, (IntPtr)p);

                ErrorCode error = GL.GetError();
                if (error != ErrorCode.NoError)
                {
                    Console.WriteLine($"OpenGL Error at TexSubImage2D: {error}");
                }

            }
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (m_program == 0) return;

        lock (_lock)
        {
            //上传最新的YUV数据到纹理
            Upload(m_texY, m_w, m_h, m_pY);
            Upload(m_texU, m_w / 2, m_h / 2, m_pU);
            Upload(m_texV, m_w / 2, m_h / 2, m_pV);
        }


        // 1. ✅ 关键修正：将逻辑像素转换为物理像素
        // 假设系统缩放为 1.5，Bounds.Width 为 100，实际物理像素是 150
        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        int canvasW = (int)(Bounds.Width * scaling);
        int canvasH = (int)(Bounds.Height * scaling);

        // 2. 计算比例
        float videoAspect = (float)m_w / m_h;    // 视频宽高比 (如 352/288)
        float canvasAspect = (float)canvasW / canvasH; // 控件宽高比

        float renderW, renderH;
        if (videoAspect > canvasAspect)
        {
            // 视频更宽：宽度填满，高度按比例缩放 (上下留黑边)
            renderW = canvasW;
            renderH = canvasW / videoAspect;
        }
        else
        {
            // 视频更窄：高度填满，宽度按比例缩放 (左右留黑边)
            renderH = canvasH;
            renderW = canvasH * videoAspect;
        }

        // 3. ✅ 精确计算居中偏移量 (关键点：x 和 y 必须是相对于左下角)
        int offsetX = (int)((canvasW - renderW) / 2);
        int offsetY = (int)((canvasH - renderH) / 2);

        // 4. 首先清除全屏为黑色 (确保黑边区域干净)
        GL.Viewport(0, 0, canvasW, canvasH);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        // 5. ✅ 设置居中的视口区域进行视频绘制
        GL.Viewport(offsetX, offsetY, (int)renderW, (int)renderH);

        // --- 后续绘图逻辑保持不变 ---

        GL.UseProgram(m_program);


        // ✅ 强制重新关联槽位，防止某些驱动下索引丢失
        GL.Uniform1(GL.GetUniformLocation(m_program, "texY"), 0);
        GL.Uniform1(GL.GetUniformLocation(m_program, "texU"), 1);
        GL.Uniform1(GL.GetUniformLocation(m_program, "texV"), 2);


        GL.BindVertexArray(m_vao);

        // 绑定纹理到正确的纹理单元
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, m_texY);

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, m_texU);

        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, m_texV);

        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Console.WriteLine($"OpenGL Error at DrawArrays: {error}");
        }

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

        if (m_program != 0)
        {
            GL.DeleteProgram(m_program);
            m_program = 0;
        }

        if (m_vao != 0)
        {
            GL.DeleteVertexArray(m_vao);
            m_vao = 0;
        }

        if (m_vbo != 0)
        {
            GL.DeleteBuffer(m_vbo);
            m_vbo = 0;
        }

        if (m_texY != 0) GL.DeleteTexture(m_texY);
        if (m_texU != 0) GL.DeleteTexture(m_texU);
        if (m_texV != 0) GL.DeleteTexture(m_texV);
    }
}