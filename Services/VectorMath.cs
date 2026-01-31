using System;

namespace pdf_chat_app.Services
{
    public static class VectorMath
    {
        public static double Cosine(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0 || a.Length != b.Length)
                return 0;

            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }

            var denom = Math.Sqrt(na) * Math.Sqrt(nb);
            return denom <= 0 ? 0 : dot / denom;
        }
    }
}
