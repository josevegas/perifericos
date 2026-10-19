using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
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
        private readonly bool _tlsInseguro;
        private readonly PerifericosHttpHost _host;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly SemaphoreSlim _candadoEnvio = new SemaphoreSlim(1, 1);

        // Extremos del transporte HTTP. Se derivan de la URL del bridge quitándole
        // el último segmento, para que la misma configuración sirva en ambos
        // transportes: …/perifericos/pinpad/bridge → …/perifericos/pinpad/agente.
        private readonly string _httpSesion = null;
        private readonly string _httpOrden = null;
        private readonly string _httpLatido = null;
        private readonly string _httpRespuesta = null;
        private double _esperaSegundos = 20;
        private double _intervaloLatidoSegundos = 20;

        private PinpadBridgeClient(
            Uri url,
            string codigoTerminal,
            string token,
            bool tlsInseguro,
            PerifericosHttpHost host)
        {
            _url = url;
            _codigoTerminal = codigoTerminal;
            _token = token;
            _tlsInseguro = tlsInseguro;
            _host = host;

            if (!EsHttp) return;
            string raiz = url.GetLeftPart(UriPartial.Path);
            int corte = raiz.LastIndexOf('/');
            if (corte > 0) raiz = raiz.Substring(0, corte);
            string caja = "?codigo_terminal=" + Uri.EscapeDataString(codigoTerminal);
            _httpSesion = raiz + "/agente";
            _httpOrden = _httpSesion + "/orden" + caja;
            _httpLatido = _httpSesion + "/latido" + caja;
            _httpRespuesta = _httpSesion + "/respuesta";
        }

        private bool EsHttp
        {
            get { return _url.Scheme == "http" || _url.Scheme == "https"; }
        }

        public static PinpadBridgeClient DesdeEntorno(PerifericosHttpHost host)
        {
            string url = Environment.GetEnvironmentVariable("PERIFERICOS_BRIDGE_URL");
            string terminal = Environment.GetEnvironmentVariable("PERIFERICOS_BRIDGE_TERMINAL");
            string token = Environment.GetEnvironmentVariable("PERIFERICOS_BRIDGE_TOKEN");
            bool inseguro = EsVerdadero(
                Environment.GetEnvironmentVariable("PERIFERICOS_BRIDGE_INSEGURO"));
            bool configurado = !string.IsNullOrWhiteSpace(url) ||
                               !string.IsNullOrWhiteSpace(terminal) ||
                               !string.IsNullOrWhiteSpace(token);
            if (!configurado) return null;

            Uri uri;
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != "wss" && uri.Scheme != "ws" &&
                 uri.Scheme != "https" && uri.Scheme != "http"))
            {
                throw new InvalidOperationException(
                    "PERIFERICOS_BRIDGE_URL debe ser una URL absoluta " +
                    "ws://, wss://, http:// o https://.");
            }
            // Fuera de loopback, ws:// y http:// dejan el token sin cifrar.
            if ((uri.Scheme == "ws" || uri.Scheme == "http") && !uri.IsLoopback && !inseguro)
                throw new InvalidOperationException(
                    "PERIFERICOS_BRIDGE_URL usa " + uri.Scheme + ":// hacia un equipo remoto. " +
                    "Use wss:// o https://, o cree bridge-insecure.txt con 1 para aceptar el riesgo.");
            if (string.IsNullOrWhiteSpace(terminal) || terminal.Trim().Length > 30)
                throw new InvalidOperationException(
                    "Configure PERIFERICOS_BRIDGE_TERMINAL con el código de esta caja.");
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException(
                    "Configure PERIFERICOS_BRIDGE_TOKEN con al menos 32 caracteres.");

            if (inseguro)
            {
                Console.Error.WriteLine(
                    "AVISO: Bridge en modo inseguro (PERIFERICOS_BRIDGE_INSEGURO): " +
                    "el token puede viajar sin cifrar y no se valida el certificado del servidor.");
            }

            var builder = new UriBuilder(uri);
            string query = builder.Query.TrimStart('?');
            string terminalQuery = "codigo_terminal=" +
                                  Uri.EscapeDataString(terminal.Trim().ToUpperInvariant());
            builder.Query = string.IsNullOrEmpty(query)
                ? terminalQuery
                : query + "&" + terminalQuery;

            return new PinpadBridgeClient(
                builder.Uri, terminal.Trim().ToUpperInvariant(), token, inseguro, host);
        }

        private static bool EsVerdadero(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return false;
            string v = valor.Trim();
            return v == "1" ||
                   string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(v, "si", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(v, "sí", StringComparison.OrdinalIgnoreCase);
        }

        // .NET Framework 4.8 no expone ClientWebSocketOptions.RemoteCertificateValidationCallback,
        // así que el override es de proceso. Se limita al host del bridge configurado para que el
        // resto de conexiones TLS del proceso conserve la validación normal.
        private void ConfigurarTlsInseguro()
        {
            if (!_tlsInseguro || (_url.Scheme != "wss" && _url.Scheme != "https")) return;
            string hostEsperado = _url.Host;
            ServicePointManager.ServerCertificateValidationCallback =
                delegate (object remitente, System.Security.Cryptography.X509Certificates.X509Certificate certificado,
                          System.Security.Cryptography.X509Certificates.X509Chain cadena,
                          SslPolicyErrors errores)
                {
                    if (errores == SslPolicyErrors.None) return true;
                    var solicitud = remitente as HttpWebRequest;
                    return solicitud != null && solicitud.RequestUri != null &&
                           string.Equals(solicitud.RequestUri.Host, hostEsperado,
                                         StringComparison.OrdinalIgnoreCase);
                };
        }

        public async Task EjecutarAsync(CancellationToken cancelar)
        {
            if (EsHttp)
                await EjecutarHttpAsync(cancelar).ConfigureAwait(false);
            else
                await EjecutarWebSocketAsync(cancelar).ConfigureAwait(false);
        }

        private async Task EjecutarWebSocketAsync(CancellationToken cancelar)
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
                        ConfigurarTlsInseguro();
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
                        "Bridge desconectado para " + _codigoTerminal + ": " + DescribirFallo(ex));
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

        /// <summary>
        /// Transporte alternativo cuando el proxy no permite el upgrade WebSocket:
        /// el mismo lease y la misma cola durable, servidos con HTTP de larga
        /// encuesta. Cada petición queda abierta hasta que hay una orden o vence
        /// el plazo (204), así que el proxy solo ve tráfico HTTP normal.
        /// </summary>
        private async Task EjecutarHttpAsync(CancellationToken cancelar)
        {
            int esperaSegundos = 1;
            while (!cancelar.IsCancellationRequested)
            {
                string conexionId = null;
                try
                {
                    ConfigurarTlsInseguro();
                    conexionId = await AbrirSesionAsync(cancelar).ConfigureAwait(false);
                    esperaSegundos = 1;
                    Console.WriteLine(
                        "Bridge conectado para el terminal " + _codigoTerminal +
                        " (HTTP de larga encuesta).");

                    while (!cancelar.IsCancellationRequested)
                    {
                        Dictionary<string, object> orden = await EsperarOrdenAsync(
                            conexionId, cancelar).ConfigureAwait(false);
                        if (orden == null) continue;
                        await EjecutarYResponderAsync(conexionId, orden, cancelar)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancelar.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        "Bridge desconectado para " + _codigoTerminal + ": " +
                        DescribirFallo(ex));
                }
                finally
                {
                    if (conexionId != null)
                        await CerrarSesionAsync(conexionId).ConfigureAwait(false);
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

        private async Task<string> AbrirSesionAsync(CancellationToken cancelar)
        {
            string cuerpo = _json.Serialize(
                new Dictionary<string, object> { { "codigo_terminal", _codigoTerminal } });
            string texto = await PeticionAsync("POST", _httpSesion, cuerpo, 20, cancelar)
                .ConfigureAwait(false);

            var sesion = _json.DeserializeObject(texto) as Dictionary<string, object>;
            object conexionId;
            if (sesion == null || !sesion.TryGetValue("conexion_id", out conexionId))
                throw new InvalidOperationException(
                    "El servidor no devolvió la sesión del agente HTTP.");
            _esperaSegundos = LeerNumero(sesion, "espera_segundos", _esperaSegundos);
            _intervaloLatidoSegundos = LeerNumero(
                sesion, "intervalo_latido", _intervaloLatidoSegundos);
            return Convert.ToString(conexionId, CultureInfo.InvariantCulture);
        }

        /// <summary>Devuelve la orden, o null si el plazo venció sin trabajo.</summary>
        private async Task<Dictionary<string, object>> EsperarOrdenAsync(
            string conexionId, CancellationToken cancelar)
        {
            string url = _httpOrden +
                "&conexion_id=" + Uri.EscapeDataString(conexionId) +
                "&espera=" + _esperaSegundos.ToString("0.#", CultureInfo.InvariantCulture);
            string texto = await PeticionAsync(
                "GET", url, null, (int)_esperaSegundos + 20, cancelar).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(texto)) return null;

            var orden = _json.DeserializeObject(texto) as Dictionary<string, object>;
            if (orden == null)
                throw new InvalidOperationException("El servidor envió una orden inválida.");
            return orden;
        }

        private static string MensajeDeFallo(
            Dictionary<string, object> orden, Exception ex)
        {
            // El pinpad se resume: sus errores traen códigos del equipo que no le
            // dicen nada al cajero, y el backend ya los guarda en su log. La
            // impresora, en cambio, falla por cosas que él mismo puede
            // arreglar —sin papel, sin cable, sin driver— y ahí el mensaje ES la
            // instrucción: ocultarlo convierte un problema de dos minutos en una
            // llamada al soporte.
            object operacionValor;
            string operacion = orden.TryGetValue("operacion", out operacionValor)
                ? Convert.ToString(operacionValor, CultureInfo.InvariantCulture)
                : null;

            if ((string.Equals(operacion, "imprimir", StringComparison.Ordinal) ||
                 string.Equals(operacion, "estado_impresora", StringComparison.Ordinal)) &&
                !string.IsNullOrWhiteSpace(ex.Message))
            {
                return ex.Message;
            }

            return "No se pudo completar la operación en el Pinpad.";
        }

        private async Task EjecutarYResponderAsync(
            string conexionId, Dictionary<string, object> orden, CancellationToken cancelar)
        {
            object id;
            if (!orden.TryGetValue("id", out id))
                throw new InvalidOperationException("La orden no tiene identificador.");

            Dictionary<string, object> resultado = null;
            string error = null;
            // El pinpad puede tardar dos minutos: sin este latido el lease vencería
            // a los 60 s y la caja quedaría fuera de línea en medio del cobro.
            using (var latidos = CancellationTokenSource.CreateLinkedTokenSource(cancelar))
            {
                Task latido = LatirAsync(conexionId, latidos.Token);
                try
                {
                    resultado = await _host.EjecutarOrdenBridgeAsync(orden).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        "No se pudo completar la orden Bridge " + id + ": " + ex);
                    error = MensajeDeFallo(orden, ex);
                }
                finally
                {
                    latidos.Cancel();
                    try
                    {
                        await latido.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }

            var respuesta = new Dictionary<string, object>
            {
                { "codigo_terminal", _codigoTerminal },
                { "conexion_id", conexionId },
                { "id", Convert.ToString(id, CultureInfo.InvariantCulture) },
            };
            if (error != null) respuesta.Add("error", error);
            else respuesta.Add("resultado", resultado);

            await PeticionAsync(
                "POST", _httpRespuesta, _json.Serialize(respuesta), 30, cancelar)
                .ConfigureAwait(false);
        }

        private async Task LatirAsync(string conexionId, CancellationToken cancelar)
        {
            string url = _httpLatido + "&conexion_id=" + Uri.EscapeDataString(conexionId);
            while (!cancelar.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_intervaloLatidoSegundos), cancelar)
                    .ConfigureAwait(false);
                try
                {
                    await PeticionAsync("POST", url, null, 15, cancelar).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Console.Error.WriteLine(
                        "No se pudo renovar el lease del Bridge: " + DescribirFallo(ex));
                }
            }
        }

        private async Task CerrarSesionAsync(string conexionId)
        {
            try
            {
                await PeticionAsync(
                    "DELETE",
                    _httpSesion + "?codigo_terminal=" +
                        Uri.EscapeDataString(_codigoTerminal) +
                        "&conexion_id=" + Uri.EscapeDataString(conexionId),
                    null, 10, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Apagando la caja: si el servidor no responde, el lease vence solo.
            }
        }

        /// <summary>Petición con el token del agente; devuelve el cuerpo ("" en 204).</summary>
        private async Task<string> PeticionAsync(
            string metodo, string url, string cuerpo, int timeoutSegundos,
            CancellationToken cancelar)
        {
            if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var peticion = (HttpWebRequest)WebRequest.Create(url);
            peticion.Method = metodo;
            peticion.Headers["X-Peripheral-Token"] = _token;
            peticion.Accept = "application/json";
            peticion.Timeout = timeoutSegundos * 1000;
            peticion.ReadWriteTimeout = timeoutSegundos * 1000;
            peticion.ServicePoint.Expect100Continue = false;

            if (cuerpo != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(cuerpo);
                peticion.ContentType = "application/json";
                peticion.ContentLength = bytes.Length;
                using (Stream flujo = await peticion.GetRequestStreamAsync()
                    .ConfigureAwait(false))
                {
                    await flujo.WriteAsync(bytes, 0, bytes.Length, cancelar)
                        .ConfigureAwait(false);
                }
            }

            using (var respuesta = (HttpWebResponse)await peticion.GetResponseAsync()
                .ConfigureAwait(false))
            {
                Stream cuerpoRespuesta = respuesta.GetResponseStream();
                if (cuerpoRespuesta == null) return string.Empty;
                using (var lector = new StreamReader(cuerpoRespuesta, Encoding.UTF8))
                {
                    return await lector.ReadToEndAsync().ConfigureAwait(false);
                }
            }
        }

        private static double LeerNumero(
            Dictionary<string, object> datos, string clave, double porDefecto)
        {
            object valor;
            if (datos == null || !datos.TryGetValue(clave, out valor) || valor == null)
                return porDefecto;
            try
            {
                return Convert.ToDouble(valor, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return porDefecto;
            }
            catch (InvalidCastException)
            {
                return porDefecto;
            }
        }

        /// <summary>
        /// Traduce el fallo del handshake a algo accionable: sin el código HTTP
        /// del servidor, un 404 (ruta inexistente) y un 403 (rechazo del agente)
        /// se ven igual —"no es posible conectar"— y no dicen qué corregir.
        /// </summary>
        private static string DescribirFallo(Exception ex)
        {
            for (Exception actual = ex; actual != null; actual = actual.InnerException)
            {
                var peticion = actual as WebException;
                var respuesta = peticion == null ? null : peticion.Response as HttpWebResponse;
                if (respuesta == null) continue;

                int codigo = (int)respuesta.StatusCode;
                string detalle = "HTTP " + codigo + " " + respuesta.StatusDescription;
                respuesta.Dispose();
                if (codigo == 401 || codigo == 403)
                    return detalle + " (el servidor rechazó al agente: revise bridge-token.txt;"
                        + " con http:// remoto el backend exige PERIFERICOS_BRIDGE_HTTP_INSEGURO"
                        + " porque el token viajaría sin cifrar)";
                if (codigo == 404)
                    return detalle + " (la ruta del agente no existe en el servidor:"
                        + " revise bridge-url.txt y que la API esté desplegada)";
                if (codigo == 409)
                    return detalle + " (el terminal ya tiene otro agente activo o el lease"
                        + " expiró: cierre la otra instancia que usa esta caja)";
                return detalle;
            }
            return ex.Message;
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
                    respuesta.Add("error", MensajeDeFallo(orden, ex));
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
