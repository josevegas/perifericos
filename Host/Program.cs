using System;
using System.Threading.Tasks;
using PuntoVenta.Services;

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
                // Completa el arranque y pareo PCL antes de aceptar solicitudes,
                // para que la primera prueba/cobro no compita con la inicialización.
                bool pinpadListo = PinpadService.AsegurarServicioEnSegundoPlano()
                    .GetAwaiter().GetResult();
                if (!pinpadListo)
                    Console.Error.WriteLine("El canal PCL no está listo; revise pinpad.log.");

                var host = new PerifericosHttpHost(token);
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
