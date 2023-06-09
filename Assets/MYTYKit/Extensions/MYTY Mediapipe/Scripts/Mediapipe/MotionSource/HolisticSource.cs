using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using MathNet.Numerics.LinearAlgebra;
using UnityEngine;
using UnityEngine.Events;
using Mediapipe;
using Mediapipe.Unity;
using MYTYKit.MotionTemplates.Mediapipe.Model;
using MYTYKit.ThirdParty.MeFaMo;
using Debug = UnityEngine.Debug;

namespace MYTYKit.MotionTemplates.Mediapipe
{
    public class HolisticSource : MonoBehaviour
    {

        static int _Counter = 0;

        static readonly GlobalInstanceTable<int, HolisticSource> _InstanceTable =
            new GlobalInstanceTable<int, HolisticSource>(20);

        readonly int m_id;
        
        public MotionSource motionSource;

        public TextAsset configText;
        public TextAsset configWithoutHandText;
        public CameraSource imageSource;

        public bool inputFlipped = true;
        public bool trackingHands = false;

        public UnityEvent<bool> detectedEvent;
        public UnityEvent<int> fpsEmitter;
        
        Stopwatch m_stopwatch;
        CalculatorGraph m_graph;

        UnityEvent<LandmarkList> m_poseWorldEvent = new();
        UnityEvent<NormalizedLandmarkList> m_poseEvent = new();
        UnityEvent<NormalizedLandmarkList> m_faceEvent = new();
        UnityEvent<NormalizedLandmarkList> m_LHEvent = new();
        UnityEvent<NormalizedLandmarkList> m_RHEvent = new();
        UnityEvent<ClassificationList> m_emotionEvent = new();

        Color32[] m_pixels;

        ResourceManager m_resourceManager;

        int m_fpsCounter = 0;
        float m_fpsTimer = 0;

        Coroutine m_fpsRoutine;
        bool m_isQuit = false;

        int m_sourceWidth;
        int m_sourceHeight;
        Texture2D m_sourceTexture;

        [SerializeField] int m_targetFps = 10;
        
        
        MeFaMoSolver m_solver;
        Vector3[] m_solverBuffer;

        HolisticSource()
        {
            m_id = System.Threading.Interlocked.Increment(ref _Counter);
            _InstanceTable.Add(m_id, this);
        }

        void Start()
        {
            m_solver = new MeFaMoSolver();
            OnRestartGraph(trackingHands);
        }

        public void SetTargetFps(int fps)
        {
            m_targetFps = fps;
        }

        public void OnRestartGraph(bool hands)
        {
            if (m_resourceManager == null) m_resourceManager = new StreamingAssetsResourceManager();

            var text = hands ? configText.text : configWithoutHandText.text;

            trackingHands = hands;

            if (m_graph == null)
            {
                m_graph = new CalculatorGraph(text);
            }
            else
            {
                m_graph.CloseAllPacketSources().AssertOk();
                m_graph.WaitUntilDone().AssertOk();
                m_graph.Dispose();
                m_graph = new CalculatorGraph(text);
            }

            var sidePacket = new SidePacket();
            sidePacket.Emplace("input_rotation", new IntPacket(180));
            sidePacket.Emplace("input_horizontally_flipped", new BoolPacket(inputFlipped));
            sidePacket.Emplace("input_vertically_flipped", new BoolPacket(false));
            sidePacket.Emplace("refine_face_landmarks", new BoolPacket(true));
            sidePacket.Emplace("model_complexity", new IntPacket((int)0));

            m_graph.ObserveOutputStream("pose_landmarks", m_id, PoseCallback, true).AssertOk();
            m_graph.ObserveOutputStream("pose_world_landmarks",m_id, PoseWorldCallback, true ).AssertOk();
            m_graph.ObserveOutputStream("face_landmarks", m_id, FaceCallback, true).AssertOk();
            if (hands)
            {
                m_graph.ObserveOutputStream("left_hand_landmarks", m_id, LHCallback, true).AssertOk();
                m_graph.ObserveOutputStream("right_hand_landmarks", m_id, RHCallback, true).AssertOk();
            }

            m_graph.ObserveOutputStream("face_emotions", m_id, EmotionCallback, true).AssertOk();

            m_poseEvent.AddListener(ProcessPose);
            m_poseWorldEvent.AddListener(ProcessPoseWorld);
            m_faceEvent.AddListener(ProcessFace);
            m_LHEvent.AddListener(ProcessLH);
            m_RHEvent.AddListener(ProcessRH);
            m_emotionEvent.AddListener(ProcessEmotion);

            m_graph.StartRun(sidePacket).AssertOk();

            m_stopwatch = new Stopwatch();
            m_stopwatch.Start();
        }

        IEnumerator FpsCount()
        {
            while (!m_isQuit)
            {
                yield return new WaitForSeconds(1.0f);
                fpsEmitter?.Invoke(m_fpsCounter);
                m_fpsCounter = 0;
            }
        }

        // Update is called once per frame
        void Update()
        {
            m_fpsTimer += Time.deltaTime;
            if (m_fpsRoutine == null) m_fpsRoutine = StartCoroutine(FpsCount());

            if (imageSource.camTexture == null || !imageSource.camTexture.didUpdateThisFrame)
            {
                if (detectedEvent != null) detectedEvent.Invoke(false);
                return;
            }

            if (m_fpsTimer < 1.0 / m_targetFps) return;

            m_fpsTimer = 0;
            m_fpsCounter++;

            if (m_sourceWidth != imageSource.camTexture.width || m_sourceHeight != imageSource.camTexture.height)
            {
                m_sourceWidth = imageSource.camTexture.width;
                m_sourceHeight = imageSource.camTexture.height;
                m_pixels = new Color32[m_sourceWidth * m_sourceHeight];

                m_sourceTexture = new Texture2D(m_sourceWidth, m_sourceHeight, TextureFormat.RGBA32, false);
            }

            imageSource.camTexture.GetPixels32(m_pixels);
            if (m_pixels == null)
            {
                Debug.Log("Null image");
                return;
            }

            m_sourceTexture.SetPixels32(m_pixels);
            m_sourceTexture.Apply();


            var mpImageFrame = new ImageFrame(ImageFormat.Types.Format.Srgba, m_sourceWidth, m_sourceHeight,
                m_sourceWidth * 4, m_sourceTexture.GetRawTextureData<byte>());
            m_graph.AddPacketToInputStream("input_video", new ImageFramePacket(mpImageFrame, GetCurrentTimestamp()))
                .AssertOk();
        }

        [AOT.MonoPInvokeCallback(typeof(CalculatorGraph.NativePacketCallback))]
        static Status.StatusArgs PoseWorldCallback(IntPtr graphPtr, int streamId, IntPtr packetPtr)
        {
            var isFound = _InstanceTable.TryGetValue(streamId, out var holistic);
            if (!isFound)
            {
                return Status.StatusArgs.FailedPrecondition("Invalid stream id");
            }
            
            using (var packet = new LandmarkListPacket(packetPtr, false))
            {
                if (!packet.IsEmpty())
                {
                    var poseWorld = packet.Get();
                    UnityMainThreadDispatcher.Instance().Enqueue(() => holistic.m_poseWorldEvent.Invoke(poseWorld));
                }
            }
            
            return Status.StatusArgs.Ok();
        }

        [AOT.MonoPInvokeCallback(typeof(CalculatorGraph.NativePacketCallback))]
        private static Status.StatusArgs PoseCallback(IntPtr graphPtr, int streamId, IntPtr packetPtr)
        {
            var isFound = _InstanceTable.TryGetValue(streamId, out var holistic);
            if (!isFound)
            {
                return Status.StatusArgs.FailedPrecondition("Invalid stream id");
            }

            using (var packet = new NormalizedLandmarkListPacket(packetPtr, false))
            {
                if (!packet.IsEmpty())
                {
                    var pose = packet.Get();
                    UnityMainThreadDispatcher.Instance().Enqueue(() => holistic.m_poseEvent.Invoke(pose));
                }
            }

            return Status.StatusArgs.Ok();
        }

        [AOT.MonoPInvokeCallback(typeof(CalculatorGraph.NativePacketCallback))]
        private static Status.StatusArgs FaceCallback(IntPtr graphPtr, int streamId, IntPtr packetPtr)
        {
            var isFound = _InstanceTable.TryGetValue(streamId, out var holistic);
            if (!isFound)
            {
                return Status.StatusArgs.FailedPrecondition("Invalid stream id");
            }

            using (var packet = new NormalizedLandmarkListPacket(packetPtr, false))
            {
                if (!packet.IsEmpty())
                {
                    var face = packet.Get();
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        holistic.m_faceEvent.Invoke(face);
                        if (holistic.detectedEvent != null) holistic.detectedEvent.Invoke(true);
                    });

                }
                else
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        if (holistic.detectedEvent != null) holistic.detectedEvent.Invoke(false);
                    });
                }
            }

            return Status.StatusArgs.Ok();
        }

        [AOT.MonoPInvokeCallback(typeof(CalculatorGraph.NativePacketCallback))]
        private static Status.StatusArgs LHCallback(IntPtr graphPtr, int streamId, IntPtr packetPtr)
        {
            var isFound = _InstanceTable.TryGetValue(streamId, out var holistic);
            if (!isFound)
            {
                return Status.StatusArgs.FailedPrecondition("Invalid stream id");
            }

            using (var packet = new NormalizedLandmarkListPacket(packetPtr, false))
            {
                if (!packet.IsEmpty())
                {
                    var hand = packet.Get();
                    UnityMainThreadDispatcher.Instance().Enqueue(() => holistic.m_LHEvent.Invoke(hand));
                }
            }

            return Status.StatusArgs.Ok();
        }

        [AOT.MonoPInvokeCallback(typeof(CalculatorGraph.NativePacketCallback))]
        private static Status.StatusArgs RHCallback(IntPtr graphPtr, int streamId, IntPtr packetPtr)
        {
            var isFound = _InstanceTable.TryGetValue(streamId, out var holistic);
            if (!isFound)
            {
                return Status.StatusArgs.FailedPrecondition("Invalid stream id");
            }

            using (var packet = new NormalizedLandmarkListPacket(packetPtr, false))
            {
                if (!packet.IsEmpty())
                {
                    var hand = packet.Get();
                    UnityMainThreadDispatcher.Instance().Enqueue(() => holistic.m_RHEvent.Invoke(hand));
                }
            }

            return Status.StatusArgs.Ok();
        }

        [AOT.MonoPInvokeCallback(typeof(CalculatorGraph.NativePacketCallback))]
        private static Status.StatusArgs EmotionCallback(IntPtr graphPtr, int streamId, IntPtr packetPtr)
        {
            var isFound = _InstanceTable.TryGetValue(streamId, out var holistic);
            if (!isFound)
            {
                return Status.StatusArgs.FailedPrecondition("Invalid stream id");
            }

            using (var packet = new ClassificationListPacket(packetPtr, false))
            {
                if (!packet.IsEmpty())
                {
                    var emotion = packet.Get();
                    UnityMainThreadDispatcher.Instance().Enqueue(() => holistic.m_emotionEvent.Invoke(emotion));
                }
            }

            return Status.StatusArgs.Ok();
        }


        private void OnDestroy()
        {
            m_isQuit = true;
            StopCoroutine(m_fpsRoutine);
            m_graph.CloseAllPacketSources().AssertOk();
            m_graph.WaitUntilDone().AssertOk();
            m_graph.Dispose();
        }

        long GetCurrentTimestampMicrosec()
        {
            return m_stopwatch == null || !m_stopwatch.IsRunning
                ? -1
                : m_stopwatch.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000);
            //return TimeSpan.TicksPerMillisecond / 1000;
        }

        Timestamp GetCurrentTimestamp()
        {
            var microsec = GetCurrentTimestampMicrosec();
            return microsec < 0 ? Timestamp.Unset() : new Timestamp(microsec);
        }

        void ProcessPose(NormalizedLandmarkList pose)
        {
            var poseRig = motionSource.GetBridgesInCategory("PoseLandmark");
            if (pose.Landmark == null) return;
            if (poseRig == null) return;
            foreach (var model in poseRig)
            {
                
                ProcessNormalized(model as MPBaseModel, pose.Landmark);
            }

        }

        void ProcessPoseWorld(LandmarkList pose)
        {
            var poseWorldBridges = motionSource.GetBridgesInCategory("PoseWorldLandmark");
            if (poseWorldBridges == null) return;
            if (pose.Landmark == null) return;
            poseWorldBridges.ForEach(bridge =>
            {
                var model = bridge as MPBaseModel;
                
                if (model == null) return;

                if (model.GetNumPoints() != pose.Landmark.Count)
                {
                    model.Alloc(pose.Landmark.Count);
                }

                int index = 0;

                foreach (var elem in pose.Landmark)
                {
                    model.SetPoint(index,
                        new Vector3(elem.X , elem.Y , elem.Z ), elem.Visibility);
                    index++;
                }
                model.Flush();
            });
        }
        
        void ProcessFace(NormalizedLandmarkList faceLM)
        {
            var faceRig = motionSource.GetBridgesInCategory("FaceLandmark");
            foreach (var model in faceRig)
            {
                ProcessNormalized(model as MPBaseModel, faceLM.Landmark);
            }

            ProcessSolverBridge("FaceSolver",faceLM.Landmark);
        }

        private void ProcessLH(NormalizedLandmarkList leftHand)
        {
            var leftHandRig = motionSource.GetBridgesInCategory("LeftHandLandmark");
            foreach (var model in leftHandRig)
            {
                ProcessNormalized(model as MPBaseModel, leftHand.Landmark);
            }
        }

        private void ProcessRH(NormalizedLandmarkList rightHand)
        {
            var rightHandRig = motionSource.GetBridgesInCategory("RightHandLandmark");
            foreach (var model in rightHandRig)
            {
                ProcessNormalized(model as MPBaseModel , rightHand.Landmark);
            }
        }

        private void ProcessEmotion(ClassificationList cflist)
        {
            foreach (var cls in cflist.Classification)
            {
                //Debug.Log("emotion "+ cls.Label);

            }
        }

        public void ProcessNormalized(MPBaseModel model, IList<NormalizedLandmark> landmarkList)
        {
            if (model == null || landmarkList == null) return;

            if (model.GetNumPoints() != landmarkList.Count)
            {
                model.Alloc(landmarkList.Count);
            }

            int index = 0;

            foreach (var elem in landmarkList)
            {
                model.SetPoint(index,
                    new Vector3(elem.X , elem.Y , elem.Z ), elem.Visibility);
                index++;
            }
            model.Flush();

        }

        public void ProcessSolverBridge(string categoryName, IList<NormalizedLandmark> landmarkList)
        {
            if (landmarkList == null) return;
            if (landmarkList.Count < 468) return;
            if (m_solverBuffer == null || m_solverBuffer.Length != landmarkList.Count)
            {
                m_solverBuffer = new Vector3[landmarkList.Count];
            }

            for (var i = 0; i < landmarkList.Count; i++)
            {
                var elem = landmarkList[i];
                m_solverBuffer[i] = new Vector3(elem.X, elem.Y, elem.Z);
            }

            var solverRig = motionSource.GetBridgesInCategory(categoryName);
            m_solver.Solve(m_solverBuffer, m_sourceWidth, m_sourceHeight);

            foreach (var model in solverRig)
            {
                var solverModel = model as MPSolverModel;
                solverModel.SetSolver(m_solver);
                solverModel.Flush();
            }
            


        }
        
        


    }
}

