using PuntoVenta.Models.Pinpad;
using PuntoVenta.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PuntoVenta.Perifericos.Host
{
    internal sealed class PinpadHttpHost
    {
        private const string Prefijo = "http://127.0.0.1:5005/";
        private const string RutaCobro = "/api/v1/pinpad/cobrar";
        private const int MaximoCuerpoBytes = 4096;
        private static readonly TimeSpan VidaClaveIdempotencia = TimeSpan.FromHours(24);

        private readonly HttpListener _listener = new HttpListener();
        private readonly PinpadService _pinpad = new PinpadService();
        private readonly string _token;
        private readonly object _candado = new object();
        private readonly Dictionary<Guid, CobroEnCache> _cobros = new Dictionary<Guid, CobroEnCache>();

        public PinpadHttpHost(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new ArgumentException("El token del servicio debe tener al menos 32 caracteres.", nameof(token));
            _token = token;
            _listener.Prefixes.Add(Prefijo);
        }

        public async Task EjecutarAsync()
        {
            _listener.Start();
            while (_listener.IsListening)
            {
                HttpListenerContext contexto = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => AtenderAsync(contexto)).ContinueWith(
                    tarea => Console.Error.WriteLine(
                        "Error no controlado del host local: " +
                        tarea.Exception.GetBaseException().Message),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private async Task AtenderAsync(HttpListenerContext contexto)
        {
            try
            {
                if (contexto.Request.RemoteEndPoint == null ||
                    !IPAddress.IsLoopback(contexto.Request.RemoteEndPoint.Address))
                {
                    await ResponderAsync(contexto, 403, Error("Solo se aceptan conexiones locales."));
                    return;
                }

                if (!TokenValido(contexto.Request.Headers["X-Peripheral-Token"]))
                {
                    await ResponderAsync(contexto, 401, Error("Token del servicio inválido."));
                    return;
                }

                if (!string.Equals(contexto.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await ResponderAsync(contexto, 405, Error("Método no permitido."));
                    return;
                }

                if (!string.Equals(contexto.Request.Url.AbsolutePath, RutaCobro, StringComparison.Ordinal))
                {
                    await ResponderAsync(contexto, 404, Error("Ruta no encontrada."));
                    return;
                }

                string claveRaw = contexto.Request.Headers["Idempotency-Key"];
                Guid clave;
                if (!Guid.TryParse(claveRaw, out clave) || clave == Guid.Empty)
                {
                    await ResponderAsync(contexto, 400, Error("Se requiere un Idempotency-Key UUID válido."));
                    return;
                }

                if (contexto.Request.ContentLength64 < 0 ||
                    contexto.Request.ContentLength64 > MaximoCuerpoBytes)
                {
                    await ResponderAsync(contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                    return;
                }

                CobroPinpadRequest solicitud;
                try
                {
                    using (var lector = new StreamReader(contexto.Request.InputStream, contexto.Request.ContentEncoding))
                    {
                        string json = await lector.ReadToEndAsync().ConfigureAwait(false);
                        solicitud = new JavaScriptSerializer().Deserialize<CobroPinpadRequest>(json);
                    }
                }
                catch (InvalidOperationException)
                {
                    await ResponderAsync(contexto, 400, Error("El cuerpo JSON no es válido."));
                    return;
                }
                catch (ArgumentException)
                {
                    await ResponderAsync(contexto, 400, Error("El cuerpo JSON no es válido."));
                    return;
                }

                if (solicitud == null || solicitud.monto <= 0)
                {
                    await ResponderAsync(contexto, 400, Error("El monto debe ser mayor a cero."));
                    return;
                }

                CobroEnCache cobro = ObtenerOCrearCobro(clave, solicitud.monto);
                if (cobro.Monto != solicitud.monto)
                {
                    await ResponderAsync(contexto, 409, Error("La clave de idempotencia ya se utilizó con otro monto."));
                    return;
                }

                CobroTarjetaResultado resultado = await cobro.Resultado.ConfigureAwait(false);
                await ResponderAsync(contexto, 200, ConvertirResultado(resultado));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error atendiendo solicitud de periféricos: " + ex);
                try
                {
                    await ResponderAsync(
                        contexto, 500, Error("Falló la operación local del pinpad."))
                        .ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    // El cliente pudo cerrar la conexión mientras el pinpad procesaba el cobro.
                }
                catch (ObjectDisposedException)
                {
                    // El cliente pudo cerrar la conexión mientras el pinpad procesaba el cobro.
                }
            }
        }

        private CobroEnCache ObtenerOCrearCobro(Guid clave, decimal monto)
        {
            lock (_candado)
            {
                DateTimeOffset ahora = DateTimeOffset.UtcNow;
                var vencidas = new List<Guid>();
                foreach (KeyValuePair<Guid, CobroEnCache> par in _cobros)
                {
                    if (par.Value.Resultado.IsCompleted &&
                        ahora - par.Value.CreadoUtc >= VidaClaveIdempotencia)
                        vencidas.Add(par.Key);
                }
                foreach (Guid vencida in vencidas)
                    _cobros.Remove(vencida);

                CobroEnCache existente;
                if (_cobros.TryGetValue(clave, out existente))
                    return existente;

                var creado = new CobroEnCache
                {
                    Monto = monto,
                    CreadoUtc = ahora,
                    Resultado = EjecutarCobroAsync(monto),
                };
                _cobros.Add(clave, creado);
                return creado;
            }
        }

        private async Task<CobroTarjetaResultado> EjecutarCobroAsync(decimal monto)
        {
            return await _pinpad.CobrarAsync(monto).ConfigureAwait(false);
        }

        private bool TokenValido(string recibido)
        {
            if (recibido == null)
                return false;

            int diferencia = recibido.Length ^ _token.Length;
            int longitud = Math.Max(recibido.Length, _token.Length);
            for (int i = 0; i < longitud; i++)
            {
                char a = i < recibido.Length ? recibido[i] : '\0';
                char b = i < _token.Length ? _token[i] : '\0';
                diferencia |= a ^ b;
            }
            return diferencia == 0;
        }

        private static Dictionary<string, object> ConvertirResultado(CobroTarjetaResultado resultado)
        {
            return new Dictionary<string, object>
            {
                { "aprobado", resultado.Aprobado },
                { "autorizacion", resultado.Autorizacion },
                { "referencia", resultado.Referencia },
                { "lote", resultado.Lote },
                { "terminal_pos", resultado.TerminalPos },
                { "captura", resultado.Captura },
                { "voucher_comercio", resultado.VoucherComercio },
                { "voucher_cliente", resultado.VoucherCliente },
                { "mensaje_error", resultado.MensajeError },
                { "codigo_respuesta", resultado.CodigoRespuesta },
                { "mensaje_equipo", resultado.MensajeEquipo },
            };
        }

        private static Dictionary<string, object> Error(string detalle)
        {
            return new Dictionary<string, object> { { "detail", detalle } };
        }

        private static async Task ResponderAsync(
            HttpListenerContext contexto, int codigo, object contenido)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(
                new JavaScriptSerializer().Serialize(contenido));
            contexto.Response.StatusCode = codigo;
            contexto.Response.ContentType = "application/json; charset=utf-8";
            contexto.Response.ContentLength64 = bytes.Length;
            contexto.Response.Headers["Cache-Control"] = "no-store";
            await contexto.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            contexto.Response.Close();
        }

        private sealed class CobroEnCache
        {
            public decimal Monto { get; set; }
            public DateTimeOffset CreadoUtc { get; set; }
            public Task<CobroTarjetaResultado> Resultado { get; set; }
        }
    }

    public sealed class CobroPinpadRequest
    {
        public decimal monto { get; set; }
    }
}
