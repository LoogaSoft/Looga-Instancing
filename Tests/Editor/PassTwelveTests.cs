using NUnit.Framework;

namespace LoogaSoft.Instancing.EditorTests
{
    public sealed class PassTwelveTests
    {
        [Test]
        public void QualityScalerPreservesAuthoredInput()
        {
            var source = new InstanceQualitySettings
            {
                Decorative = true,
                Density = 0.8f,
                MinimumPixels = 0.5f,
                ShadowDistance = 400,
                MinimumShadowLod = 1
            };
            InstanceQualitySettings result = InstanceQualityScaler.Scale(source, 0.5f);
            Assert.That(source.Density, Is.EqualTo(0.8f));
            Assert.That(source.ShadowDistance, Is.EqualTo(400));
            Assert.That(result.Density, Is.EqualTo(0.6f).Within(0.001f));
            Assert.That(result.ShadowDistance, Is.EqualTo(310).Within(0.001f));
            Assert.That(result.MinimumShadowLod, Is.EqualTo(2));
        }

        [Test]
        public void AdaptivePolicyUsesHysteresisAndBounds()
        {
            Assert.That(AdaptiveQualityController.CalculateNextScale(1, 12, 10, 0.55f, 0.1f, 0.05f, 1.05f, 0.85f), Is.EqualTo(0.9f));
            Assert.That(AdaptiveQualityController.CalculateNextScale(0.9f, 9, 10, 0.55f, 0.1f, 0.05f, 1.05f, 0.85f), Is.EqualTo(0.9f));
            Assert.That(AdaptiveQualityController.CalculateNextScale(0.9f, 7, 10, 0.55f, 0.1f, 0.05f, 1.05f, 0.85f), Is.EqualTo(0.95f));
            Assert.That(AdaptiveQualityController.CalculateNextScale(0.55f, 20, 10, 0.55f, 0.1f, 0.05f, 1.05f, 0.85f), Is.EqualTo(0.55f));
        }
    }
}
