using System;
using System.Threading.Tasks;

namespace PuntoVenta.Perifericos.Host
{
    internal static class Program
    {
        private static void Main()
        {
            string token = Environment.GetEnvironmentVariable("PERIFERICOS_TOKEN");
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
            {
                Console.Error.WriteLine(
                    "Configure PERIFERICOS_TOKEN con al menos 32 caracteres antes de iniciar.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                var host = new PinpadHttpHost(token);
                Console.WriteLine("Servicio de periféricos escuchando en http://127.0.0.1:5005/");
                host.EjecutarAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("No se pudo iniciar el servicio de periféricos: " + ex.Message);
                Environment.ExitCode = 1;
            }
        }
    }
}
