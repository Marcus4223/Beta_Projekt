using System.Globalization;
using System.IO.Ports;
using System.Text;
using PpgReader;

// Skal passe med SAMPLE_PERIOD_US i ESP32-sketchen (4000 µs = 250 Hz).
const int SampleRateHz = 250;
const int BaudRate = 115200;
const long ExpectedPeriodUs = 1_000_000 / SampleRateHz;
const long StatusIntervalUs = 5_000_000;

// Sæt til true, hvis pulsslagene peger nedad i kurven (afhænger af kredsløbet - tjek fx i WaveForms).
const bool InvertSignal = false;

Console.OutputEncoding = Encoding.UTF8;

// Brug:  dotnet run               -> læser live fra COM3
//        dotnet run -- COM5       -> læser live fra en anden port
//        dotnet run -- fil.csv    -> afspiller en tidligere optagelse (uden ESP32)
string source = args.Length > 0 ? args[0] : "COM3";
bool replay = File.Exists(source);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

SerialPort? port = null;
StreamWriter? rawFile = null;
StreamWriter? ibiFile = null;
string? rawPath = null;
string? ibiPath = null;
IEnumerable<string> lines;

if (replay)
{
    Console.WriteLine($"Afspiller {source} ...");
    lines = File.ReadLines(source);
}
else
{
    port = OpenPort(source);
    if (port is null)
        return 1;

    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    Directory.CreateDirectory("optagelser");
    rawPath = Path.Combine("optagelser", $"ppg_{stamp}.csv");
    ibiPath = Path.Combine("optagelser", $"ibi_{stamp}.csv");
    rawFile = new StreamWriter(rawPath);
    ibiFile = new StreamWriter(ibiPath);
    rawFile.WriteLine("t_us,ppg");
    ibiFile.WriteLine("beat_tid_s,ibi_ms");

    Console.WriteLine($"Forbundet til {source} ({BaudRate} baud). Tryk Ctrl+C for at stoppe.");
    Console.WriteLine($"Gemmer rådata i {rawPath}");
    lines = ReadSerialLines(port, cts.Token);
}

var processor = new PpgProcessor(SampleRateHz, InvertSignal);
long? lastRawT = null;
long tUs = 0;                 // tid siden første måling, i µs
long samples = 0;
long lost = 0;
long skippedLines = 0;
long nextStatusUs = StatusIntervalUs;
long windowStartUs = 0;
long windowIntervals = 0;
int windowMin = int.MaxValue;
int windowMax = int.MinValue;

try
{
    foreach (string line in lines)
    {
        if (cts.IsCancellationRequested)
            break;

        // Linjer der ikke er "tid,værdi" (fx opstartsbeskeder fra ESP32'en) springes over.
        if (!TryParseSample(line, out long t, out int ppg))
        {
            skippedLines++;
            continue;
        }

        if (lastRawT is long prev)
        {
            // micros() på ESP32'en løber rundt efter ca. 71 min. Med uint-subtraktion bliver forskellen stadig rigtig.
            long dt = unchecked((uint)(t - prev));
            if (dt > 1_000_000)
            {
                Console.WriteLine("Spring i tidsstemplerne (er ESP32'en genstartet?)");
                dt = ExpectedPeriodUs;
            }
            else
            {
                lost += Math.Max(0, (long)Math.Round((double)dt / ExpectedPeriodUs) - 1);
            }
            tUs += dt;
            windowIntervals++;
        }
        lastRawT = t;
        samples++;
        windowMin = Math.Min(windowMin, ppg);
        windowMax = Math.Max(windowMax, ppg);

        rawFile?.WriteLine($"{tUs},{ppg}");

        if (processor.Add(tUs / 1e6, ppg) is Beat beat)
        {
            Console.WriteLine($"  ♥ IBI {beat.IbiMs,5:F0} ms  ({60_000 / beat.IbiMs:F0} BPM)");
            ibiFile?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{beat.TimeSeconds:F4},{beat.IbiMs:F1}"));
        }

        if (tUs >= nextStatusUs)
        {
            double rate = windowIntervals / ((tUs - windowStartUs) / 1e6);
            string bpm = processor.HeartRateBpm is double b ? $"{b:F0} BPM" : "–";
            string rmssd = processor.RmssdMs is double r ? $"{r:F0} ms" : "–";
            Console.WriteLine($"[{tUs / 1e6,4:F0} s] {rate:F1} Hz | ADC {windowMin}-{windowMax} | tabte samples: {lost} | puls: {bpm} | RMSSD: {rmssd}");
            if (windowMin <= 0 || windowMax >= 4095)
                Console.WriteLine("  Advarsel: signalet rammer ADC'ens grænse (0 eller 4095). Tjek spændingsniveauet.");

            windowStartUs = tUs;
            windowIntervals = 0;
            windowMin = int.MaxValue;
            windowMax = int.MinValue;
            nextStatusUs += StatusIntervalUs;
        }
    }
}
catch (Exception ex) when (ex is IOException or InvalidOperationException)
{
    Console.WriteLine($"Forbindelsen til {source} blev afbrudt: {ex.Message}");
}
finally
{
    port?.Dispose();
    rawFile?.Dispose();
    ibiFile?.Dispose();
}

Console.WriteLine();
Console.WriteLine($"Færdig: {samples} samples ({tUs / 1e6:F1} s), {lost} tabte samples, {processor.Ibis.Count} slag fundet.");
if (skippedLines > 0)
    Console.WriteLine($"{skippedLines} linjer var ikke data og blev sprunget over.");
if (processor.Ibis.Count > 0)
    Console.WriteLine($"Gennemsnitlig puls: {60_000 / processor.Ibis.Average():F1} BPM");
if (processor.RmssdMs is double finalRmssd)
    Console.WriteLine($"RMSSD (seneste 30 slag): {finalRmssd:F1} ms");
if (rawPath is not null)
    Console.WriteLine($"Data gemt i {rawPath} og {ibiPath}");
return 0;

static SerialPort? OpenPort(string name)
{
    var port = new SerialPort(name, BaudRate)
    {
        NewLine = "\n",
        ReadTimeout = 2000,
        // RTS og DTR styrer reset/boot på ESP32-boardet. Er de slået fra, kører ESP32'en normalt.
        RtsEnable = false,
        DtrEnable = false,
    };

    try
    {
        port.Open();
        port.DiscardInBuffer();
        return port;
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine($"{name} er optaget af et andet program. Luk Serial Monitor/Plotter i Arduino IDE og prøv igen.");
    }
    catch (Exception ex) when (ex is IOException or ArgumentException)
    {
        Console.WriteLine($"Kunne ikke åbne {name}: {ex.Message}");
        Console.WriteLine($"Tilgængelige porte: {string.Join(", ", SerialPort.GetPortNames())}");
    }

    port.Dispose();
    return null;
}

static IEnumerable<string> ReadSerialLines(SerialPort port, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        string? line = null;
        try
        {
            line = port.ReadLine();
        }
        catch (TimeoutException)
        {
            Console.WriteLine("Ingen data fra ESP32'en i 2 sekunder. Er sketchen uploadet, og er baudraten 115200?");
        }

        if (line is not null)
            yield return line;
    }
}

static bool TryParseSample(string line, out long t, out int ppg)
{
    t = 0;
    ppg = 0;
    string[] parts = line.Trim().Split(',');
    return parts.Length >= 2
        && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out t)
        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ppg);
}
