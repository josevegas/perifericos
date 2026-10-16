using System;
using System.Threading;
using System.Threading.Tasks;
using PuntoVenta.Services;

namespace PuntoVenta.Perifericos.Host
{
    internal static class Program
    {
        // Una instancia por sesión de Windows. El nombre lleva el puerto porque
        // lo que se protege es el 5005, no el archivo ejecutable.
        private const string NombreInstancia = @"Local\SmartOne.Perifericos.Host.5005";

        // Vive lo que vive el proceso: si se recolectara, el bloqueo se soltaría
        // y una segunda copia se creería la única.
        private static readonly Mutex Instancia = new Mutex(false, NombreInstancia);

        private static void Main()
        {
            // El reintento de la tarea programada puede lanzar esta copia antes
            // de que la anterior termine de soltar el puerto. Sale sin error, en
            // vez de competir por el 5005 y dejar un fallo en host.log.
            if (!TomarInstancia())
            {
                Console.WriteLine(
                    "Ya hay un servicio de periféricos en marcha en esta sesión de Windows; " +
                    "esta copia no arranca.");
                return;
            }

            try
            {
                Ejecutar();
            }
            finally
            {
                Instancia.ReleaseMutex();
            }
        }

        private static bool TomarInstancia()
        {
            try
            {
                return Instancia.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                // La instancia anterior murió sin soltarlo: el bloqueo es nuestro.
                return true;
            }
        }

        private static void Ejecutar()
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
                PinpadBridgeClient bridge = PinpadBridgeClient.DesdeEntorno(host);
                if (bridge != null)
                {
                    Console.WriteLine("Iniciando conexión saliente segura del agente Bridge...");
                    Task.Run(() => bridge.EjecutarAsync(CancellationToken.None));
                }
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
