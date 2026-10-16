using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PuntoVenta.Perifericos.Host
{
    /// <summary>Canal saliente TLS entre esta caja y la cola durable del backend.</summary>
    internal sealed class PinpadBridgeClient
    {
        private const int MaximoMensajeBytes = 128 * 1024;
        private static readonly TimeSpan IntervaloLatido = TimeSpan.FromSeconds(20);

        private readonly Uri _url;
        private readonly string _codigoTerminal;
        private readonly string _token;
        private readonly PerifericosHttpHost _host;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly SemaphoreSlim _candadoEnvio = new SemaphoreSlim(1, 1);

        private PinpadBridgeClient(
            Uri url,
            string codigoTerminal,
            string token,
            PerifericosHttpHost host)
        {
            _url = url;
            _codigoTerminal = codigoTerminal;
            _token = token;
            _host = host;
        }

        public static PinpadBridgeClient DesdeEntorno(PerifericosHttpHost host)
        {
            string url = Environment.GetEnvironmentVariable("PERIFERICOS_BRIDGE_URL");
            string terminal = Environment.GetEnvironmentVariable("PERIFERICOS_BRIDGE_TERMINAL");
            string token = Environment.GetEnvironmentVariable("PERIFERICOS_BRIDGE_TOKEN");
            bool configurado = !string.IsNullOrWhiteSpace(url) ||
                               !string.IsNullOrWhiteSpace(terminal) ||
                               !string.IsNullOrWhiteSpace(token);
            if (!configurado) return null;

            Uri uri;
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != "wss" && !(uri.Scheme == "ws" && uri.IsLoopback)))
            {
                throw new InvalidOperationException(
                    "PERIFERICOS_BRIDGE_URL debe usar wss:// (ws:// solo en loopback).");
            }
            if (string.IsNullOrWhiteSpace(terminal) || terminal.Trim().Length > 30)
                throw new InvalidOperationException(
                    "Configure PERIFERICOS_BRIDGE_TERMINAL con el código de esta caja.");
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException(
                    "Configure PERIFERICOS_BRIDGE_TOKEN con al menos 32 caracteres.");

            var builder = new UriBuilder(uri);
            string query = builder.Query.TrimStart('?');
            string terminalQuery = "codigo_terminal=" +
                                  Uri.EscapeDataString(terminal.Trim().ToUpperInvariant());
            builder.Query = string.IsNullOrEmpty(query)
                ? terminalQuery
                : query + "&" + terminalQuery;

            return new PinpadBridgeClient(
                builder.Uri, terminal.Trim().ToUpperInvariant(), token, host);
        }

        public async Task EjecutarAsync(CancellationToken cancelar)
        {
            int esperaSegundos = 1;
            while (!cancelar.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.SetRequestHeader("X-Peripheral-Token", _token);
                        socket.Options.KeepAliveInterval = IntervaloLatido;
                        await socket.ConnectAsync(_url, cancelar).ConfigureAwait(false);
                        Console.WriteLine(
                            "Bridge conectado para el terminal " + _codigoTerminal + ".");
                        esperaSegundos = 1;

                        using (var latidos = CancellationTokenSource.CreateLinkedTokenSource(cancelar))
                        {
                            Task recibir = RecibirOrdenesAsync(socket, cancelar);
                            Task enviarLatidos = EnviarLatidosAsync(socket, latidos.Token);
                            Task terminada = await Task.WhenAny(recibir, enviarLatidos)
                                .ConfigureAwait(false);
                            latidos.Cancel();
                            if (terminada == enviarLatidos)
                            {
                                await enviarLatidos.ConfigureAwait(false);
                                socket.Abort();
                            }
                            else
                            {
                                try
                                {
                                    await enviarLatidos.ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (latidos.IsCancellationRequested)
                                {
                                }
                            }
                            await recibir.ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancelar.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        "Bridge desconectado para " + _codigoTerminal + ": " + ex.Message);
                }

                if (cancelar.IsCancellationRequested) return;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(esperaSegundos), cancelar)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                esperaSegundos = Math.Min(30, esperaSegundos * 2);
            }
        }

        private async Task RecibirOrdenesAsync(
            ClientWebSocket socket, CancellationToken cancelar)
        {
            while (socket.State == WebSocketState.Open && !cancelar.IsCancellationRequested)
            {
                string texto = await RecibirMensajeAsync(socket, cancelar).ConfigureAwait(false);
                if (texto == null) return;

                Dictionary<string, object> orden =
                    _json.DeserializeObject(texto) as Dictionary<string, object>;
                if (orden == null)
                    throw new InvalidOperationException("El servidor envió una orden inválida.");

                object id;
                if (!orden.TryGetValue("id", out id))
                    throw new InvalidOperationException("La orden no tiene identificador.");

                var respuesta = new Dictionary<string, object>
                {
                    { "id", Convert.ToString(id, CultureInfo.InvariantCulture) },
                };
                try
                {
                    respuesta.Add("resultado", await _host.EjecutarOrdenBridgeAsync(orden)
                        .ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        "No se pudo completar la orden Bridge " + id + ": " + ex);
                    respuesta.Add("error", "No se pudo completar la operación en el Pinpad.");
                }

                await EnviarMensajeAsync(socket, respuesta, cancelar).ConfigureAwait(false);
            }
        }

        private async Task EnviarLatidosAsync(
            ClientWebSocket socket, CancellationToken cancelar)
        {
            while (!cancelar.IsCancellationRequested)
            {
                await Task.Delay(IntervaloLatido, cancelar).ConfigureAwait(false);
                await EnviarMensajeAsync(
                    socket,
                    new Dictionary<string, object> { { "tipo", "latido" } },
                    cancelar).ConfigureAwait(false);
            }
        }

        private async Task<string> RecibirMensajeAsync(
            ClientWebSocket socket, CancellationToken cancelar)
        {
            var buffer = new byte[16 * 1024];
            using (var contenido = new MemoryStream())
            {
                WebSocketReceiveResult resultado;
                do
                {
                    resultado = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancelar).ConfigureAwait(false);
                    if (resultado.MessageType == WebSocketMessageType.Close)
                        return null;
                    contenido.Write(buffer, 0, resultado.Count);
                    if (contenido.Length > MaximoMensajeBytes)
                        throw new InvalidOperationException("La orden excede el tamaño permitido.");
                }
                while (!resultado.EndOfMessage);

                if (resultado.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("El servidor envió un mensaje no textual.");
                return Encoding.UTF8.GetString(contenido.ToArray());
            }
        }

        private async Task EnviarMensajeAsync(
            ClientWebSocket socket,
            Dictionary<string, object> mensaje,
            CancellationToken cancelar)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_json.Serialize(mensaje));
            await _candadoEnvio.WaitAsync(cancelar).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    cancelar).ConfigureAwait(false);
            }
            finally
            {
                _candadoEnvio.Release();
            }
        }
    }
}
