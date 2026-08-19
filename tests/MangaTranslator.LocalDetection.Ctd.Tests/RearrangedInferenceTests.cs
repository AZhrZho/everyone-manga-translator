using OpenCvSharp;

namespace MangaTranslator.LocalDetection.Ctd.Tests;

public sealed class RearrangedInferenceTests
{
    [Theory]
    [InlineData(4000, 200, 2040, 102)]
    [InlineData(200, 4000, 102, 2040)]
    public void Extreme_aspect_ratio_is_rearranged_and_restored(
        int height,
        int width,
        int expectedMapHeight,
        int expectedMapWidth)
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.Black);
        var calls = 0;

        var result = RearrangedInference.TryInfer(
            image,
            inputSize: 1024,
            scaleThreshold: 2.5,
            aspectThreshold: 3,
            _ =>
            {
                calls++;
                return new ModelInferenceMaps(
                    new float[2 * 1024 * 1024],
                    2,
                    1024,
                    1024,
                    new float[1024 * 1024],
                    1024,
                    1024);
            });

        Assert.NotNull(result);
        Assert.Equal(1, calls);
        Assert.Equal(expectedMapHeight, result.LineHeight);
        Assert.Equal(expectedMapWidth, result.LineWidth);
        Assert.Equal(expectedMapHeight, result.MaskHeight);
        Assert.Equal(expectedMapWidth, result.MaskWidth);
    }

    [Fact]
    public void Ordinary_page_does_not_use_rearranged_inference()
    {
        using var image = new Mat(1400, 1000, MatType.CV_8UC3, Scalar.Black);

        var result = RearrangedInference.TryInfer(
            image,
            inputSize: 1024,
            scaleThreshold: 2.5,
            aspectThreshold: 3,
            _ => throw new InvalidOperationException("Inference should not run."));

        Assert.Null(result);
    }

    [Fact]
    public void Multiple_rearranged_batches_are_stitched_without_padding_artifacts()
    {
        const int inputSize = 64;
        using var image = new Mat(5000, 20, MatType.CV_8UC3, Scalar.Black);
        var calls = 0;

        var result = RearrangedInference.TryInfer(
            image,
            inputSize,
            scaleThreshold: 2.5,
            aspectThreshold: 3,
            _ =>
            {
                calls++;
                return new ModelInferenceMaps(
                    new float[2 * inputSize * inputSize],
                    2,
                    inputSize,
                    inputSize,
                    new float[inputSize * inputSize],
                    inputSize,
                    inputSize);
            });

        Assert.NotNull(result);
        Assert.Equal(7, calls);
        Assert.Equal(2500, result.LineHeight);
        Assert.Equal(10, result.LineWidth);
    }
}
