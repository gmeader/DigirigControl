using System.IO.Ports;
using System.Text;

namespace DigiRigControlCenter.Services;

public sealed class CatService
{
    public string[] GetAvailablePorts() => SerialPort.GetPortNames()
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public byte[] TestCommand(string portName, int baudRate, int dataBits, StopBits stopBits, string protocol, string command)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new InvalidOperationException("Select a COM port first.");
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("Enter a CAT command first.");

        byte[] request = protocol.Equals("icom-civ", StringComparison.OrdinalIgnoreCase)
            ? ParseHex(command)
            : Encoding.ASCII.GetBytes(command);

        using var port = new SerialPort(portName, baudRate, Parity.None, dataBits, stopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = 1200,
            WriteTimeout = 1200,
            Encoding = Encoding.ASCII,
            DtrEnable = false,
            RtsEnable = false
        };

        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        port.Write(request, 0, request.Length);

        var response = new List<byte>();
        var deadline = DateTime.UtcNow.AddMilliseconds(1200);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(30);
            int count = port.BytesToRead;
            if (count <= 0) continue;
            var buffer = new byte[count];
            int read = port.Read(buffer, 0, count);
            response.AddRange(buffer.Take(read));

            if (protocol.Equals("icom-civ", StringComparison.OrdinalIgnoreCase))
            {
                if (response.Contains((byte)0xFD)) break;
            }
            else if (response.Contains((byte)';')) break;
        }
        return response.ToArray();
    }

    private static byte[] ParseHex(string text)
    {
        var compact = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
                          .Replace(" ", "").Replace("-", "").Replace(":", "");
        if (compact.Length == 0 || compact.Length % 2 != 0)
            throw new FormatException("CI-V command must contain complete hexadecimal byte pairs.");
        var bytes = new byte[compact.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
        return bytes;
    }
}
