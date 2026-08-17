using System;
using UnityEngine;

namespace VRChat.MmdPreview.Pmx2Fbx
{
    [Serializable]
    public struct MmdBezierCurve
    {
        public static readonly MmdBezierCurve Linear = new MmdBezierCurve(0f, 0f, 1f, 1f);

        public float X1;
        public float Y1;
        public float X2;
        public float Y2;

        public MmdBezierCurve(float x1, float y1, float x2, float y2)
        {
            X1 = Mathf.Clamp01(x1);
            Y1 = Mathf.Clamp01(y1);
            X2 = Mathf.Clamp01(x2);
            Y2 = Mathf.Clamp01(y2);
        }

        public float Evaluate(float time)
        {
            time = Mathf.Clamp01(time);
            if (time <= 0f || time >= 1f) return time;
            if (Mathf.Abs(X1 - Y1) < 1e-6f && Mathf.Abs(X2 - Y2) < 1e-6f) return time;

            float parameter = time;
            for (int iteration = 0; iteration < 8; iteration++)
            {
                float x = Cubic(parameter, X1, X2) - time;
                if (Mathf.Abs(x) < 1e-6f) return Cubic(parameter, Y1, Y2);
                float derivative = CubicDerivative(parameter, X1, X2);
                if (Mathf.Abs(derivative) < 1e-6f) break;
                float candidate = parameter - x / derivative;
                if (candidate < 0f || candidate > 1f) break;
                parameter = candidate;
            }

            float low = 0f;
            float high = 1f;
            for (int iteration = 0; iteration < 18; iteration++)
            {
                parameter = (low + high) * 0.5f;
                if (Cubic(parameter, X1, X2) < time) low = parameter;
                else high = parameter;
            }
            return Cubic((low + high) * 0.5f, Y1, Y2);
        }

        private static float Cubic(float t, float p1, float p2)
        {
            float inverse = 1f - t;
            return 3f * inverse * inverse * t * p1 + 3f * inverse * t * t * p2 + t * t * t;
        }

        private static float CubicDerivative(float t, float p1, float p2)
        {
            float inverse = 1f - t;
            return 3f * inverse * inverse * p1 + 6f * inverse * t * (p2 - p1) + 3f * t * t * (1f - p2);
        }
    }
}
