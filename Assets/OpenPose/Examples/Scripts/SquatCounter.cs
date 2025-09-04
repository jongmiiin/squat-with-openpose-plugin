// SquatCounter.cs (C# 2.0 / Unity 안전 업데이트용)
using UnityEngine;
using UnityEngine.UI;

namespace OpenPose.Example
{
    public class SquatCounter : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] Text squatText;          // 횟수 표시
        [SerializeField] Text formText;           // 자세 피드백
        void Start()
        {
            // 씬에서 "SquatCount"라는 이름의 오브젝트를 찾아 Text 컴포넌트를 가져옵니다.
            squatText = GameObject.Find("SquatCount").GetComponent<Text>();
            formText = GameObject.Find("Form").GetComponent<Text>();
        }
        public Image overlayHint;       // 이미지 힌트(선택)
        public float overlayFade = 0.5f;

        [Header("Thresholds")]
        public float kneeAngleDown = 95f;
        public float kneeAngleUp = 160f;
        public float hipDropRatio = 0.78f;
        public float backTiltMax = 25f;
        public float kneeOverToeMax = 0.35f;

        [Header("Smoothing")]
        public int emaWindow = 6;
        public float minConf = 0.1f;

        private int count = 0;
        private bool isDown = false;
        [SerializeField] private float emaHipY = -1f;

        private bool baselineReady = false;
        [SerializeField] private float baselineHipY = -1f;

        private int badFormFrames = 0, goodFormFrames = 0;
        private const int FEEDBACK_HOLD = 6;

        private float overlayAlpha = 0f;

        // ------------------ 유틸 함수 ------------------
        private static float AngleDeg(Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 ab = a - b;
            Vector2 cb = c - b;
            return Vector2.Angle(ab, cb);
        }

        private static float Ema(float prev, float cur, float k)
        {
            if (prev < 0f) return cur;
            return prev * (1f - k) + cur * k;
        }

        private bool TryGet(ref OPDatum d, int bi, int kp, out Vector2 v)
        {
            v = Vector2.zero;
            if (d.poseKeypoints == null) return false;
            if (bi >= d.poseKeypoints.GetSize(0)) return false;
            if (kp >= d.poseKeypoints.GetSize(1)) return false;

            float x = d.poseKeypoints.Get(bi, kp, 0);
            float y = d.poseKeypoints.Get(bi, kp, 1);
            float c = d.poseKeypoints.Get(bi, kp, 2);

            if (c < minConf) return false;
            v = new Vector2(x, y);
            return true;
        }

        // ------------------ 메인 업데이트 ------------------
        public void UpdateFromDatum(ref OPDatum d, int bodyIndex)
        {
            Debug.Log("스쿼트 카운터 업데이트 중...");
            Vector2 midHip;
            if (!TryGet(ref d, bodyIndex, 8, out midHip)) return;

            Vector2 rHip, rKnee, rAnk, lHip, lKnee, lAnk;
            bool haveR = TryGet(ref d, bodyIndex, 9, out rHip) &
                         TryGet(ref d, bodyIndex, 10, out rKnee) &
                         TryGet(ref d, bodyIndex, 11, out rAnk);
            bool haveL = TryGet(ref d, bodyIndex, 12, out lHip) &
                         TryGet(ref d, bodyIndex, 13, out lKnee) &
                         TryGet(ref d, bodyIndex, 14, out lAnk);

            // 기준값 자동 캘리브레이션
            if (!baselineReady && (haveR || haveL))
            {
                float rk = haveR ? AngleDeg(rHip, rKnee, rAnk) : 180f;
                float lk = haveL ? AngleDeg(lHip, lKnee, lAnk) : 180f;
                if (rk > 165f && lk > 165f)
                {
                    baselineHipY = midHip.y;
                    baselineReady = true;
                }
            }

            // 힙 EMA
            float k = 2f / (emaWindow + 1f);
            emaHipY = Ema(emaHipY, midHip.y, k);

            // 깊이 판정
            bool deepByKnee = false, deepByHip = false;
            if (haveR) deepByKnee |= AngleDeg(rHip, rKnee, rAnk) < kneeAngleDown;
            if (haveL) deepByKnee |= AngleDeg(lHip, lKnee, lAnk) < kneeAngleDown;

            if (baselineReady)
            {
                float dropRatio = baselineHipY > 0.001f ? (emaHipY / baselineHipY) : 1f;
                deepByHip = dropRatio > (1f / hipDropRatio);
            }

            bool deep = deepByKnee || deepByHip;

            // 올라옴 판정
            bool up = true;
            if (haveR) up &= AngleDeg(rHip, rKnee, rAnk) > kneeAngleUp;
            if (haveL) up &= AngleDeg(lHip, lKnee, lAnk) > kneeAngleUp;

            // 상태머신
            if (!isDown && deep) isDown = true;
            else if (isDown && up)
            {
                isDown = false;
                count++;
                if (squatText != null) squatText.text = "Squats: " + count;
                Debug.Log("Squats: " + count);
            }

            // 올바른 자세 판정
            bool backOk = true;
            Vector2 neck;
            if (TryGet(ref d, bodyIndex, 1, out neck))
            {
                Vector2 torso = (neck - midHip).normalized;
                float tilt = Vector2.Angle(torso, new Vector2(0f, 1f));
                backOk = tilt <= backTiltMax;
            }

            bool kneesOk = true;
            if (haveR)
            {
                float thighR = Vector2.Distance(rHip, rKnee);
                float overR = Mathf.Abs(rKnee.x - rAnk.x) / Mathf.Max(thighR, 0.001f);
                kneesOk &= overR <= kneeOverToeMax;
            }
            if (haveL)
            {
                float thighL = Vector2.Distance(lHip, lKnee);
                float overL = Mathf.Abs(lKnee.x - lAnk.x) / Mathf.Max(thighL, 0.001f);
                kneesOk &= overL <= kneeOverToeMax;
            }

            bool depthOk = deepByKnee || deepByHip;

            int okCount = (backOk ? 1 : 0) + (kneesOk ? 1 : 0) + (depthOk ? 1 : 0);
            bool good = okCount >= 3;

            if (good) { goodFormFrames++; badFormFrames = 0; }
            else { badFormFrames++; goodFormFrames = 0; }

            // overlay 표시 여부 저장 (LateUpdate에서 안전하게 적용)
            overlayAlpha = 0f;
            if (badFormFrames >= FEEDBACK_HOLD) overlayAlpha = overlayFade;

            // formText 즉시 업데이트 (LateUpdate에서도 동기화 가능)
            if (formText != null)
            {
                if (badFormFrames >= FEEDBACK_HOLD)
                {
                    if (!backOk) formText.text = "등을 곧게 펴세요";
                    else if (!kneesOk) formText.text = "무릎이 발끝을 넘지 않게";
                    else if (!depthOk) formText.text = "조금 더 앉아보세요";
                    else formText.text = "";
                }
                else if (goodFormFrames >= FEEDBACK_HOLD)
                {
                    formText.text = "좋아요!";
                }
            }
        }

        // ------------------ LateUpdate에서 안전하게 Renderer 적용 ------------------
        void LateUpdate()
        {
            if (overlayHint != null)
            {
                Color c = overlayHint.color;
                overlayHint.color = new Color(c.r, c.g, c.b, overlayAlpha);
            }
        }

        public void ResetCounter()
        {
            count = 0;
            isDown = false;
            emaHipY = -1f;
            baselineReady = false;
            badFormFrames = goodFormFrames = 0;
            overlayAlpha = 0f;
            if (squatText != null) squatText.text = "Squats: 0";
            if (formText != null) formText.text = "";
        }

        public int Count
        {
            get { return count; }
        }
    }
}
