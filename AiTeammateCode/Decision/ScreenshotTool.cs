using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace AiTeammate.AiTeammateCode.Decision;

/// <summary>
/// Captures the current rendered game frame as a JPEG data URL for the <c>screenshot</c> tool.
/// Runs on the main thread (the tool dispatcher already marshals there). The same
/// <c>Viewport.GetTexture().GetImage()</c> path is used by the StupidScreenshot mod, so it works
/// with the game's renderer; the image is downscaled to keep vision-token cost reasonable.
/// 截取当前游戏画面并编码为 data URL，供支持视觉的模型查看。
/// </summary>
public static class ScreenshotTool
{
    /// <summary>Longest edge of the captured image. Larger images cost far more tokens.</summary>
    public const int MaxDimension = 1024;

    private const float JpegQuality = 0.72f;

    public sealed class Result
    {
        public bool IsSuccess { get; init; }
        public string DataUrl { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public string Error { get; init; } = "";
    }

    public static Result Capture()
    {
        try
        {
            var viewport = NGame.Instance?.GetViewport();
            if (viewport == null)
                return Fail("游戏窗口尚未准备好，无法截图。");

            var image = viewport.GetTexture()?.GetImage();
            if (image == null)
                return Fail("无法读取当前画面（渲染器没有返回图像）。");

            if (image.IsEmpty())
                return Fail("当前画面为空，无法截图。");

            var width = image.GetWidth();
            var height = image.GetHeight();
            var longest = Math.Max(width, height);
            if (longest > MaxDimension)
            {
                var scale = MaxDimension / (float)longest;
                width = Math.Max(1, (int)MathF.Round(width * scale));
                height = Math.Max(1, (int)MathF.Round(height * scale));
                image.Resize(width, height, Image.Interpolation.Bilinear);
            }

            if (image.GetFormat() != Image.Format.Rgb8)
                image.Convert(Image.Format.Rgb8);

            var jpeg = image.SaveJpgToBuffer(JpegQuality);
            if (jpeg == null || jpeg.Length == 0)
                return Fail("图像编码失败。");

            return new Result
            {
                IsSuccess = true,
                DataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                Width = width,
                Height = height
            };
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Screenshot] 截取失败: {ex.Message}");
            return Fail($"截图失败：{ex.Message}");
        }
    }

    private static Result Fail(string error) => new() { IsSuccess = false, Error = error };
}
