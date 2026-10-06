namespace PpgReader;

/// <summary>Glidende gennemsnit over de seneste N værdier.</summary>
public class MovingAverage
{
    private readonly double[] _buffer;
    private int _index;
    private int _count;
    private double _sum;

    public MovingAverage(int length)
    {
        _buffer = new double[Math.Max(1, length)];
    }

    public double Add(double value)
    {
        if (_count == _buffer.Length)
            _sum -= _buffer[_index];
        else
            _count++;

        _buffer[_index] = value;
        _sum += value;
        _index = (_index + 1) % _buffer.Length;
        return _sum / _count;
    }
}
