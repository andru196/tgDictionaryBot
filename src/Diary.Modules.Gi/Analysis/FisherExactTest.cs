namespace Diary.Modules.Gi.Analysis;

/// <summary>
/// Односторонний точный тест Фишера для таблицы 2×2. Считается в коде, а не моделью:
/// цифры в отчёте обязаны быть одинаковыми при повторном прогоне и проверяемыми построчно.
/// </summary>
/// <remarks>
/// Таблица:
/// <code>
///                симптом был   симптома не было
///   ел продукт        a               b
///   не ел             c               d
/// </code>
/// Возвращается вероятность увидеть наблюдаемую или более выраженную связь при условии,
/// что связи нет.
/// </remarks>
public static class FisherExactTest
{
    public static double RightTailPValue(int a, int b, int c, int d)
    {
        if (a < 0 || b < 0 || c < 0 || d < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(a), "Частоты в таблице не могут быть отрицательными.");
        }

        var rowFood = a + b;
        var rowOther = c + d;
        var colSymptom = a + c;
        var total = rowFood + rowOther;

        if (total == 0 || rowFood == 0 || rowOther == 0 || colSymptom == 0 || (b + d) == 0)
        {
            return 1.0;
        }

        // Суммируем вероятности всех таблиц, где связь не слабее наблюдаемой.
        var maxA = Math.Min(rowFood, colSymptom);
        var p = 0.0;
        for (var i = a; i <= maxA; i++)
        {
            var bi = rowFood - i;
            var ci = colSymptom - i;
            var di = rowOther - ci;
            if (bi < 0 || ci < 0 || di < 0)
            {
                continue;
            }

            p += HypergeometricProbability(i, bi, ci, di);
        }

        return Math.Clamp(p, 0.0, 1.0);
    }

    private static double HypergeometricProbability(int a, int b, int c, int d)
    {
        var total = a + b + c + d;

        // Через логарифмы: факториалы даже на сотне наблюдений переполняют double.
        var logP = LogFactorial(a + b) + LogFactorial(c + d) + LogFactorial(a + c) + LogFactorial(b + d)
                   - LogFactorial(total)
                   - LogFactorial(a) - LogFactorial(b) - LogFactorial(c) - LogFactorial(d);

        return Math.Exp(logP);
    }

    private static double LogFactorial(int n) => n <= 1 ? 0.0 : LogGamma(n + 1.0);

    /// <summary>Аппроксимация Ланцоша, точности с запасом для наших объёмов.</summary>
    private static double LogGamma(double x)
    {
        ReadOnlySpan<double> coefficients =
        [
            76.18009172947146, -86.50532032941677, 24.01409824083091,
            -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5,
        ];

        var y = x;
        var tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);

        var series = 1.000000000190015;
        for (var j = 0; j < coefficients.Length; j++)
        {
            series += coefficients[j] / ++y;
        }

        return -tmp + Math.Log(2.5066282746310005 * series / x);
    }
}
