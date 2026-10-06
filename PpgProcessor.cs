namespace PpgReader;

/// <summary>Et fundet hjerteslag: tidspunkt og tid siden forrige slag (IBI).</summary>
public readonly record struct Beat(double TimeSeconds, double IbiMs);

/// <summary>
/// Filtrerer det rå PPG-signal og finder hjerteslag.
/// Tiden mellem slag (IBI, inter-beat interval) er grundlaget for HRV.
///
/// Slagene findes ud fra signalets hældning: Den stejleste stigning i hver pulsbølge er starten af slaget.
/// Hældningen påvirkes næsten ikke af langsom drift (vejrtrækning, bevægelse), og den mindre
/// dikrote bølge efter hvert slag stiger langt mindre stejlt, så den ikke tælles med.
/// </summary>
public class PpgProcessor
{
    // Et slag skal ligge mellem 300 og 2000 ms efter det forrige (200-30 BPM).
    private const double MinIbiMs = 300;
    private const double MaxIbiMs = 2000;

    // Et slag tælles, når hældningen passerer denne andel af de seneste pulsbølgers største hældning.
    private const double ThresholdFraction = 0.5;

    // Hældninger under dette (ADC-enheder pr. 40 ms) regnes som støj, fx når der ingen finger er på sensoren.
    private const double MinSlope = 5;

    private const int RecentBeatsForBpm = 5;
    private const int RecentBeatsForHrv = 30;

    private readonly MovingAverage _smooth;
    private readonly double[] _recent;          // de seneste udglattede værdier, til at beregne hældningen
    private readonly double _sign;
    private readonly double _amplitudeDecay;
    private readonly int _warmupSamples;
    private readonly List<double> _ibis = new();

    private int _recentIndex;
    private int _sampleCount;
    private double _amplitude;
    private double _prevSlope;
    private double _prevT;
    private bool _armed;
    private double? _lastBeatT;

    /// <param name="sampleRateHz">Samplingsfrekvensen fra ESP32'en.</param>
    /// <param name="invert">True hvis pulsslagene peger nedad i signalet (afhænger af kredsløbet).</param>
    public PpgProcessor(int sampleRateHz, bool invert = false)
    {
        _smooth = new MovingAverage(sampleRateHz / 10);         // 100 ms: fjerner støj (pulsbølgen ligger under ca. 5 Hz)
        _recent = new double[sampleRateHz / 25];                // hældning over 40 ms
        _sign = invert ? -1 : 1;
        _amplitudeDecay = Math.Exp(-1.0 / (4.0 * sampleRateHz)); // største hældning glemmes langsomt (over ca. 4 s)
        _warmupSamples = 2 * sampleRateHz;
    }

    public IReadOnlyList<double> Ibis => _ibis;

    /// <summary>Puls i slag pr. minut ud fra de seneste slag, eller null hvis der ikke er nok slag endnu.</summary>
    public double? HeartRateBpm =>
        _ibis.Count < RecentBeatsForBpm ? null : 60_000 / _ibis.TakeLast(RecentBeatsForBpm).Average();

    /// <summary>
    /// RMSSD (root mean square of successive differences) i ms over de seneste slag.
    /// Et af de mest brugte korttids-mål for HRV.
    /// </summary>
    public double? RmssdMs
    {
        get
        {
            if (_ibis.Count < 10)
                return null;
            var recent = _ibis.TakeLast(RecentBeatsForHrv).ToArray();
            double sumSquares = 0;
            for (int i = 1; i < recent.Length; i++)
                sumSquares += Math.Pow(recent[i] - recent[i - 1], 2);
            return Math.Sqrt(sumSquares / (recent.Length - 1));
        }
    }

    /// <summary>Tilføjer en måling. Returnerer et slag, hvis der blev fundet et nyt.</summary>
    public Beat? Add(double tSeconds, int raw)
    {
        double smooth = _smooth.Add(raw);
        if (_sampleCount == 0)
            Array.Fill(_recent, smooth);

        double slope = _sign * (smooth - _recent[_recentIndex]);
        _recent[_recentIndex] = smooth;
        _recentIndex = (_recentIndex + 1) % _recent.Length;

        _amplitude = Math.Max(slope, _amplitude * _amplitudeDecay);
        _sampleCount++;

        Beat? beat = null;
        if (_sampleCount > _warmupSamples && _amplitude >= MinSlope)
        {
            double threshold = ThresholdFraction * _amplitude;

            // Signalet skal være på vej ned mellem to slag, før et nyt slag kan tælles.
            if (slope < 0)
                _armed = true;

            if (_armed && slope >= threshold)
            {
                _armed = false;
                // Lineær interpolation mellem de to målinger giver et mere præcist tidspunkt end samplingen alene.
                double fraction = Math.Clamp((threshold - _prevSlope) / (slope - _prevSlope), 0, 1);
                beat = RegisterCrossing(_prevT + fraction * (tSeconds - _prevT));
            }
        }

        _prevSlope = slope;
        _prevT = tSeconds;
        return beat;
    }

    private Beat? RegisterCrossing(double tCross)
    {
        if (_lastBeatT is not double last)
        {
            _lastBeatT = tCross;
            return null;
        }

        double ibiMs = (tCross - last) * 1000;
        if (ibiMs < MinIbiMs)
            return null;                 // for tæt på forrige slag: sandsynligvis støj

        _lastBeatT = tCross;
        if (ibiMs > MaxIbiMs)
            return null;                 // signalet har været væk; start forfra fra dette slag

        _ibis.Add(ibiMs);
        return new Beat(tCross, ibiMs);
    }
}
