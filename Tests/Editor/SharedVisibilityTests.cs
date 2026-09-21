using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Instancing.Tests
{
    public sealed class SharedVisibilityTests
    {
        [TestCase(CameraType.Game, false, false,
            InstanceVisibilityCameraKind.Game)]
        [TestCase(CameraType.Game, true, false,
            InstanceVisibilityCameraKind.Scope)]
        [TestCase(CameraType.Game, false, true,
            InstanceVisibilityCameraKind.Stack)]
        [TestCase(CameraType.SceneView, false, false,
            InstanceVisibilityCameraKind.Scene)]
        [TestCase(CameraType.Reflection, false, false,
            InstanceVisibilityCameraKind.Reflection)]
        [TestCase(CameraType.Preview, false, false,
            InstanceVisibilityCameraKind.Preview)]
        public void CameraKindsCoverQualifiedScopes(CameraType type, bool scope,
            bool stack, InstanceVisibilityCameraKind expected)
        {
            Assert.AreEqual(expected,
                InstanceOcclusion.Classify(type, scope, stack));
        }

        [Test]
        public void QualifiedCameraTypesFailClosedForStereoAndVr()
        {
            Assert.IsTrue(InstanceOcclusion.IsEligible(CameraType.Game, false));
            Assert.IsTrue(InstanceOcclusion.IsEligible(CameraType.SceneView, false));
            Assert.IsTrue(InstanceOcclusion.IsEligible(CameraType.Reflection, false));
            Assert.IsTrue(InstanceOcclusion.IsEligible(CameraType.Preview, false));
            Assert.IsFalse(InstanceOcclusion.IsEligible(CameraType.Game, true));
            Assert.IsFalse(InstanceOcclusion.IsEligible(CameraType.VR, false));
        }

        [Test]
        public void CompatibleStackUsesBaseCameraContext()
        {
            var baseObject = new GameObject("Visibility base camera");
            var overlayObject = new GameObject("Visibility overlay camera");
            Camera baseCamera = baseObject.AddComponent<Camera>();
            Camera overlay = overlayObject.AddComponent<Camera>();
            overlay.CopyFrom(baseCamera);
            using var feature = InstanceOcclusion.Acquire();
            try
            {
                Assert.IsTrue(InstanceOcclusion.CanShareStack(overlay, baseCamera));
                InstanceOcclusion.RegisterCamera(overlay, baseCamera,
                    InstanceVisibilityCameraKind.Stack);
                InstanceOcclusion.Publish(baseCamera, baseCamera,
                    Texture2D.blackTexture, Matrix4x4.identity,
                    baseCamera.worldToCameraMatrix, baseCamera.projectionMatrix,
                    4, 4, 3, 0.0001f, 2, 5, 0.25f,
                    false, 0.2f, 0.15f, 120, 8192);

                Assert.IsTrue(InstanceVisibility.TryGet(overlay,
                    out InstanceVisibilityContext context));
                Assert.AreSame(overlay, context.Camera);
                Assert.AreSame(baseCamera, context.ScopeCamera);
                Assert.AreEqual(InstanceVisibilityCameraKind.Stack,
                    context.CameraKind);
                Assert.AreSame(Texture2D.blackTexture, context.DepthPyramid);
            }
            finally
            {
                InstanceOcclusion.Invalidate(overlay);
                InstanceOcclusion.Invalidate(baseCamera);
                Object.DestroyImmediate(overlayObject);
                Object.DestroyImmediate(baseObject);
            }
        }

        [Test]
        public void CaptureClaimRejectsDuplicateWorkForOneScope()
        {
            var root = new GameObject("Visibility claim camera");
            Camera camera = root.AddComponent<Camera>();
            using var feature = InstanceOcclusion.Acquire();
            try
            {
                long before = InstanceVisibility.GetDiagnostics()
                    .DuplicateCapturesSkipped;
                Assert.IsTrue(InstanceOcclusion.TryBeginCapture(camera, camera,
                    InstanceVisibilityCameraKind.Game));
                Assert.IsFalse(InstanceOcclusion.TryBeginCapture(camera, camera,
                    InstanceVisibilityCameraKind.Game));
                Assert.AreEqual(before + 1,
                    InstanceVisibility.GetDiagnostics().DuplicateCapturesSkipped);
            }
            finally
            {
                InstanceOcclusion.Invalidate(camera);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ConsumerKindsDescribeFutureSharedUsers()
        {
            InstanceVisibilityConsumerKind before = InstanceVisibility
                .GetDiagnostics().ConsumerKinds;
            using (InstanceVisibility.RegisterConsumer(
                InstanceVisibilityConsumerKind.Impostors
                | InstanceVisibilityConsumerKind.Hlod))
            {
                InstanceVisibilityConsumerKind active = InstanceVisibility
                    .GetDiagnostics().ConsumerKinds;
                Assert.AreNotEqual(0,
                    active & InstanceVisibilityConsumerKind.Impostors);
                Assert.AreNotEqual(0,
                    active & InstanceVisibilityConsumerKind.Hlod);
            }
            Assert.AreEqual(before, InstanceVisibility.GetDiagnostics().ConsumerKinds);
        }
    }
}
