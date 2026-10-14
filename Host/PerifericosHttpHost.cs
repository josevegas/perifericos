using PuntoVenta.Infrastructure.Impresion;
using PuntoVenta.Models.Impresion;
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
    /// <summary>
    /// Host HTTP local que le da al backend (FastAPI) acceso a los periféricos
    /// de la caja: el pinpad y la impresora de tickets.
    ///
    /// Escucha SÓLO en 127.0.0.1 y exige <c>X-Peripheral-Token</c>: no tiene
    /// sentido de red, es un canal entre dos procesos de la misma máquina. Se
    /// hace por HTTP y no por carga de DLL porque el backend es Python, y no se
    /// hace por cola de impresión porque el pinpad no es una impresora.
    /// </summary>
    internal sealed class PerifericosHttpHost
    {
        private const string Prefijo = "http://127.0.0.1:5005/";

        /// <summary>Rutas: pinpad y impresora. El método de todas es POST.</summary>
        private const string RutaCobro = "/api/v1/pinpad/cobrar";
        private const string RutaComprobante = "/api/v1/impresion/comprobante";
        private const string RutaGaveta = "/api/v1/impresion/gaveta";
        private const string RutaEstado = "/api/v1/impresion/estado";

        /// <summary>El cobro es un solo número: 4 KB sobra y acota el abuso.</summary>
        private const int MaximoCobroBytes = 4096;

        /// <summary>
        /// El comprobante trae la venta, el emisor, el contexto y los pagos.
        /// 64 KB es holgado para eso y sigue siendo un techo.
        /// </summary>
        private const int MaximoComprobanteBytes = 64 * 1024;

        /// <summary>Gaveta y estado no llevan cuerpo, pero se tolera uno mínimo.</summary>
        private const int MaximoCuerpoCortoBytes = 1024;

        private static readonly TimeSpan VidaClaveIdempotencia = TimeSpan.FromHours(24);

        private readonly HttpListener _listener = new HttpListener();
        private readonly PinpadService _pinpad = new PinpadService();
        private readonly string _token;
        private readonly object _candado = new object();
        private readonly Dictionary<Guid, CobroEnCache> _cobros = new Dictionary<Guid, CobroEnCache>();
        private readonly Dictionary<Guid, ImpresionEnCache> _impresiones =
            new Dictionary<Guid, ImpresionEnCache>();

        public PerifericosHttpHost(string token)
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

        /// <summary>
        /// Filtros comunes a todo lo que entra, y después despacho por ruta.
        ///
        /// El orden de los filtros es parte del contrato y no se toca: token
        /// antes que método, método antes que ruta. Un GET a una ruta inventada
        /// responde 405 y no 404, y el backend ya cuenta con eso.
        /// </summary>
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

                switch (contexto.Request.Url.AbsolutePath)
                {
                    case RutaCobro:
                        await AtenderCobroAsync(contexto);
                        return;
                    case RutaComprobante:
                        await AtenderComprobanteAsync(contexto);
                        return;
                    case RutaGaveta:
                        await AtenderGavetaAsync(contexto);
                        return;
                    case RutaEstado:
                        await AtenderEstadoAsync(contexto);
                        return;
                    default:
                        await ResponderAsync(contexto, 404, Error("Ruta no encontrada."));
                        return;
                }
            }
            catch (ImpresionException ex)
            {
                // La impresión falla por cosas que el operador puede arreglar
                // (no hay térmica, está apagada, papel trabado). El mensaje de
                // la librería ya lo dice en castellano y sin traza: se pasa tal
                // cual, que es lo que hace útil el diagnóstico del backend.
                Console.Error.WriteLine("Impresión rechazada: " + ex.Message);
                await ResponderSeguroAsync(contexto, 500, Error(ex.Message));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "Error atendiendo solicitud de periféricos (" +
                    contexto.Request.Url.AbsolutePath + "): " + ex);
                await ResponderSeguroAsync(
                    contexto, 500, Error(MensajeDeFallo(contexto.Request.Url.AbsolutePath)));
            }
        }

        /// <summary>Qué se le dice al backend cuando falla algo que no es del equipo.</summary>
        private static string MensajeDeFallo(string ruta)
        {
            switch (ruta)
            {
                case RutaComprobante:
                    return "Falló la impresión local del comprobante.";
                case RutaGaveta:
                    return "No se pudo abrir la gaveta.";
                case RutaEstado:
                    return "No se pudo consultar la impresora.";
                default:
                    return "Falló la operación local del pinpad.";
            }
        }

        // ---------------------------------------------------------------- cobro

        private async Task AtenderCobroAsync(HttpListenerContext contexto)
        {
            Guid clave;
            if (!ClaveIdempotencia(contexto, out clave))
            {
                await ResponderAsync(contexto, 400, Error("Se requiere un Idempotency-Key UUID válido."));
                return;
            }

            if (!CuerpoDentroDelLimite(contexto, MaximoCobroBytes))
            {
                await ResponderAsync(contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                return;
            }

            CobroPinpadRequest solicitud = await LeerCuerpoAsync<CobroPinpadRequest>(contexto)
                .ConfigureAwait(false);

            if (solicitud == null)
            {
                await ResponderAsync(contexto, 400, Error("El cuerpo JSON no es válido."));
                return;
            }

            if (solicitud.monto <= 0)
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

        private CobroEnCache ObtenerOCrearCobro(Guid clave, decimal monto)
        {
            lock (_candado)
            {
                DateTimeOffset ahora = DateTimeOffset.UtcNow;
                PurgarVencidas(ahora);

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

        // ----------------------------------------------------------- impresión

        /// <summary>
        /// Imprime el comprobante de una venta que ya está registrada.
        ///
        /// Usa la misma <c>Idempotency-Key</c> que el cobro de esa venta, y eso
        /// es deliberado: una venta es una clave, y cada endpoint deduplica por
        /// su lado. Si el backend manda el cobro y después la impresión con la
        /// misma clave, se cobra una vez y se imprime una vez.
        ///
        /// Reintentar con la misma clave NO vuelve a imprimir: devuelve lo que
        /// ya se imprimió con <c>ya_impreso: true</c>. Eso cubre el caso feo —
        /// el papel salió pero la respuesta se perdió en el camino. Volver a
        /// imprimir a propósito es cosa del operador y se pide con
        /// <c>copia: true</c> sobre una clave nueva: así el segundo papel se
        /// distingue a simple vista del original.
        ///
        /// A diferencia del cobro, acá SÓLO se memoriza lo que salió bien. Un
        /// cobro fallido no se reintenta con la misma clave —reintentaría la
        /// venta—, pero una impresión fallida sí: el cajero prende la
        /// impresora, carga papel y reintenta. Si se memorizara el fallo, esa
        /// clave quedaría envenenada 24 horas.
        /// </summary>
        private async Task AtenderComprobanteAsync(HttpListenerContext contexto)
        {
            Guid clave;
            if (!ClaveIdempotencia(contexto, out clave))
            {
                await ResponderAsync(contexto, 400, Error("Se requiere un Idempotency-Key UUID válido."));
                return;
            }

            if (!CuerpoDentroDelLimite(contexto, MaximoComprobanteBytes))
            {
                await ResponderAsync(contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                return;
            }

            ImpresionComprobanteRequest solicitud =
                await LeerCuerpoAsync<ImpresionComprobanteRequest>(contexto).ConfigureAwait(false);

            if (solicitud == null)
            {
                await ResponderAsync(contexto, 400, Error("El cuerpo JSON no es válido."));
                return;
            }

            if (solicitud.venta == null || solicitud.emisor == null || solicitud.contexto == null)
            {
                await ResponderAsync(
                    contexto, 400, Error("Se requieren 'venta', 'emisor' y 'contexto'."));
                return;
            }

            if (string.IsNullOrWhiteSpace(solicitud.venta.serie_numero))
            {
                await ResponderAsync(contexto, 400, Error("La venta debe traer 'serie_numero'."));
                return;
            }

            // Se valida con el propio predicado del modelo en vez de repetir la
            // regla acá: el ticket sin RUC o sin razón social no es un ticket.
            EmisorImpresion emisor = solicitud.emisor.AModelo();
            if (!emisor.PuedeEmitir())
            {
                await ResponderAsync(
                    contexto, 400, Error("El emisor debe traer 'ruc' y 'razon_social'."));
                return;
            }

            ComprobanteImpresion venta = solicitud.venta.AModelo();
            if (venta == null)
            {
                await ResponderAsync(contexto, 400, Error("La venta debe traer una 'linea'."));
                return;
            }

            ImpresionEnCache impresion;
            bool impresoAhora = false;

            if (!BuscarImpresion(clave, out impresion))
            {
                // La impresora se resuelve ANTES de memorizar la clave: si no
                // hay ninguna, la clave no se gasta y el reintento con el mismo
                // UUID vuelve a intentar de verdad.
                string impresora = ImpresionService.Impresora();
                ContextoImpresion contextoImpresion = solicitud.contexto.AModelo();

                impresion = ObtenerOCrearImpresion(
                    clave, impresora, venta, emisor, contextoImpresion, solicitud.copia,
                    out impresoAhora);
            }

            if (!impresion.Coincide(venta, solicitud.copia))
            {
                await ResponderAsync(
                    contexto, 409,
                    Error("La clave de idempotencia ya se utilizó para otro comprobante."));
                return;
            }

            await EsperarImpresionAsync(contexto, clave, impresion, impresoAhora);
        }

        /// <summary>
        /// Espera el resultado memorizado y responde. Si la impresión falló, se
        /// olvida la clave para que el reintento pueda imprimir otra vez.
        /// </summary>
        private async Task EsperarImpresionAsync(
            HttpListenerContext contexto, Guid clave, ImpresionEnCache impresion, bool impresoAhora)
        {
            ResultadoImpresion resultado;
            try
            {
                resultado = await impresion.Resultado.ConfigureAwait(false);
            }
            catch
            {
                OlvidarImpresion(clave, impresion);
                throw;
            }

            await ResponderAsync(contexto, 200, new Dictionary<string, object>
            {
                { "impreso", true },
                { "ya_impreso", !impresoAhora },
                { "impresora", resultado.Impresora },
                { "serie_numero", impresion.SerieNumero },
                { "copia", impresion.Copia },
            });
        }

        private bool BuscarImpresion(Guid clave, out ImpresionEnCache impresion)
        {
            lock (_candado)
            {
                PurgarVencidas(DateTimeOffset.UtcNow);
                return _impresiones.TryGetValue(clave, out impresion);
            }
        }

        private ImpresionEnCache ObtenerOCrearImpresion(
            Guid clave, string impresora, ComprobanteImpresion venta, EmisorImpresion emisor,
            ContextoImpresion contexto, bool copia, out bool creada)
        {
            lock (_candado)
            {
                PurgarVencidas(DateTimeOffset.UtcNow);

                // Sólo la petición que gana la carrera arranca la impresión; la
                // otra se queda con la misma tarea en vez de sacar dos papeles.
                ImpresionEnCache existente;
                if (_impresiones.TryGetValue(clave, out existente))
                {
                    creada = false;
                    return existente;
                }

                var nueva = new ImpresionEnCache
                {
                    SerieNumero = venta.SerieNumero,
                    Copia = copia,
                    CreadoUtc = DateTimeOffset.UtcNow,
                    Resultado = EjecutarImpresionAsync(impresora, venta, emisor, contexto, copia),
                };
                _impresiones.Add(clave, nueva);
                creada = true;
                return nueva;
            }
        }

        private void OlvidarImpresion(Guid clave, ImpresionEnCache impresion)
        {
            lock (_candado)
            {
                ImpresionEnCache actual;
                if (_impresiones.TryGetValue(clave, out actual) && ReferenceEquals(actual, impresion))
                    _impresiones.Remove(clave);
            }
        }

        private static Task<ResultadoImpresion> EjecutarImpresionAsync(
            string impresora, ComprobanteImpresion venta, EmisorImpresion emisor,
            ContextoImpresion contexto, bool copia)
        {
            return Task.Run(() =>
            {
                // La impresora ya viene resuelta: se le pasa para que el
                // servicio no vuelva a resolverla y el nombre informado sea
                // exactamente el que recibió el trabajo.
                new ImpresionService(impresora).Imprimir(venta, emisor, contexto, copia);
                return new ResultadoImpresion { Impresora = impresora };
            });
        }

        /// <summary>
        /// Abre la gaveta sin imprimir nada.
        ///
        /// Sin clave de idempotencia: abrir dos veces la misma gaveta no deja
        /// nada a medias, y el backend a veces sólo quiere el cajón (dar
        /// cambio, guardar un depósito) sin venta de por medio.
        /// </summary>
        private async Task AtenderGavetaAsync(HttpListenerContext contexto)
        {
            if (!CuerpoDentroDelLimite(contexto, MaximoCuerpoCortoBytes))
            {
                await ResponderAsync(contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                return;
            }

            string impresora = ImpresionService.Impresora();
            new ImpresionService(impresora).AbrirGaveta();

            await ResponderAsync(contexto, 200, new Dictionary<string, object>
            {
                { "abierta", true },
                { "impresora", impresora },
            });
        }

        /// <summary>
        /// Diagnóstico de la impresora: a qué cola se manda y con qué formato.
        ///
        /// Responde 200 aunque no haya impresora —la consulta se contestó bien—
        /// con <c>disponible: false</c> y el motivo. Sirve para saber desde el
        /// backend por qué no sale un ticket sin gastar un papel.
        /// </summary>
        private async Task AtenderEstadoAsync(HttpListenerContext contexto)
        {
            if (!CuerpoDentroDelLimite(contexto, MaximoCuerpoCortoBytes))
            {
                await ResponderAsync(contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                return;
            }

            string impresora = null;
            string detalle = null;
            try
            {
                impresora = ImpresionService.Impresora();
            }
            catch (ImpresionException ex)
            {
                detalle = ex.Message;
            }

            await ResponderAsync(contexto, 200, new Dictionary<string, object>
            {
                { "disponible", impresora != null },
                { "impresora", impresora },
                { "detalle", detalle },
                { "columnas", EscPos.Columnas },
                { "pagina_codigos", EscPos.PaginaCodigos },
            });
        }

        // ------------------------------------------------------------ comunes

        private static bool ClaveIdempotencia(HttpListenerContext contexto, out Guid clave)
        {
            string claveRaw = contexto.Request.Headers["Idempotency-Key"];
            return Guid.TryParse(claveRaw, out clave) && clave != Guid.Empty;
        }

        private static bool CuerpoDentroDelLimite(HttpListenerContext contexto, int maximoBytes)
        {
            return contexto.Request.ContentLength64 >= 0 &&
                   contexto.Request.ContentLength64 <= maximoBytes;
        }

        /// <summary>
        /// Lee y deserializa el cuerpo. Devuelve <c>null</c> si el JSON no sirve,
        /// que quien llame traduce a 400.
        /// </summary>
        private static async Task<T> LeerCuerpoAsync<T>(HttpListenerContext contexto) where T : class
        {
            try
            {
                using (var lector = new StreamReader(
                    contexto.Request.InputStream, contexto.Request.ContentEncoding))
                {
                    string json = await lector.ReadToEndAsync().ConfigureAwait(false);
                    return new JavaScriptSerializer().Deserialize<T>(json);
                }
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Descarta las claves ya vencidas. Se llama con el candado tomado.</summary>
        private void PurgarVencidas(DateTimeOffset ahora)
        {
            var vencidas = new List<Guid>();

            foreach (KeyValuePair<Guid, CobroEnCache> par in _cobros)
            {
                if (par.Value.Resultado.IsCompleted &&
                    ahora - par.Value.CreadoUtc >= VidaClaveIdempotencia)
                    vencidas.Add(par.Key);
            }
            foreach (Guid vencida in vencidas)
                _cobros.Remove(vencida);

            vencidas.Clear();
            foreach (KeyValuePair<Guid, ImpresionEnCache> par in _impresiones)
            {
                if (par.Value.Resultado.IsCompleted &&
                    ahora - par.Value.CreadoUtc >= VidaClaveIdempotencia)
                    vencidas.Add(par.Key);
            }
            foreach (Guid vencida in vencidas)
                _impresiones.Remove(vencida);
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

        private static async Task ResponderSeguroAsync(
            HttpListenerContext contexto, int codigo, object contenido)
        {
            try
            {
                await ResponderAsync(contexto, codigo, contenido).ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // El backend ya se fue. Igual la operación local terminó (o falló)
                // y quedó registrada; no hay a quién avisarle.
            }
            catch (ObjectDisposedException)
            {
                // Idem: la conexión se cerró mientras se trabajaba.
            }
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

        private sealed class ImpresionEnCache
        {
            public string SerieNumero { get; set; }
            public bool Copia { get; set; }
            public DateTimeOffset CreadoUtc { get; set; }
            public Task<ResultadoImpresion> Resultado { get; set; }

            /// <summary>Mismo comprobante y mismo tipo de papel, o no es un reintento.</summary>
            public bool Coincide(ComprobanteImpresion venta, bool copia)
            {
                return Copia == copia &&
                       string.Equals(
                           SerieNumero ?? string.Empty,
                           venta.SerieNumero ?? string.Empty,
                           StringComparison.Ordinal);
            }
        }

        private sealed class ResultadoImpresion
        {
            public string Impresora { get; set; }
        }
    }
}
