using PuntoVenta.Infrastructure.Impresion;
using PuntoVenta.Models.Impresion;
using PuntoVenta.Models.Pinpad;
using PuntoVenta.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const string RutaAnulacion = "/api/v1/pinpad/anular";
        private const string RutaPruebaPinpad = "/api/v1/pinpad/probar";
        private const string RutaParametrosTms = "/api/v1/pinpad/parametros";
        private const string RutaActualizarTms = "/api/v1/pinpad/actualizar-tms";
        private const string RutaInicializar = "/api/v1/pinpad/inicializar";
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
        private readonly Dictionary<Guid, AnulacionEnCache> _anulaciones =
            new Dictionary<Guid, AnulacionEnCache>();
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
                    case RutaAnulacion:
                        await AtenderAnulacionAsync(contexto);
                        return;
                    case RutaPruebaPinpad:
                        await AtenderPruebaPinpadAsync(contexto);
                        return;
                    case RutaParametrosTms:
                        await AtenderParametrosTmsAsync(contexto);
                        return;
                    case RutaActualizarTms:
                        await AtenderActualizarTmsAsync(contexto);
                        return;
                    case RutaInicializar:
                        await AtenderInicializarAsync(contexto);
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
                case RutaAnulacion:
                    return "Falló la anulación local del cobro en el pinpad.";
                default:
                    return "Falló la operación local del pinpad.";
            }
        }

        // ---------------------------------------------------------------- cobro

        private async Task AtenderPruebaPinpadAsync(HttpListenerContext contexto)
        {
            if (!CuerpoDentroDelLimite(contexto, MaximoCuerpoCortoBytes))
            {
                await ResponderAsync(
                    contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                return;
            }

            CobroTarjetaResultado resultado = await _pinpad.ProbarEcoAsync().ConfigureAwait(false);
            bool respuestaRecibida = !string.IsNullOrEmpty(resultado.RespuestaCruda);

            await ResponderAsync(contexto, 200, new Dictionary<string, object>
            {
                { "comunicacion_ok", resultado.Aprobado && respuestaRecibida },
                { "respuesta_recibida", respuestaRecibida },
                { "mensaje_error", resultado.MensajeError },
            });
        }

        /// <summary>
        /// Configuración TMS del terminal y números de serie. Sólo lectura: sirve
        /// para contrastar con Niubiz si el comercio tiene habilitadas las
        /// billeteras que muestra el equipo.
        /// </summary>
        private async Task AtenderParametrosTmsAsync(HttpListenerContext contexto)
        {
            Dictionary<string, object> datos = _pinpad.ConsultarParametrosTms();
            await ResponderAsync(contexto, 200, datos);
        }

        /// <summary>
        /// Fuerza una descarga TMS en el terminal. Es una escritura: mientras el
        /// equipo actualiza no se puede cobrar.
        /// </summary>
        private async Task AtenderActualizarTmsAsync(HttpListenerContext contexto)
        {
            Dictionary<string, object> datos = _pinpad.IniciarActualizacionTms();
            await ResponderAsync(contexto, datos.ContainsKey("iniciada") && (bool)datos["iniciada"] ? 202 : 409, datos);
        }

        /// <summary>
        /// Inicialización del terminal (operación 38): el equipo se conecta al
        /// host de Niubiz y recarga parámetros. No cierra el lote — la venta
        /// pendiente sigue en él — pero sí deja el equipo ocupado unos segundos.
        /// </summary>
        private async Task AtenderInicializarAsync(HttpListenerContext contexto)
        {
            if (!CuerpoDentroDelLimite(contexto, MaximoCuerpoCortoBytes))
            {
                await ResponderAsync(
                    contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                return;
            }

            CobroTarjetaResultado resultado = await _pinpad.InicializarAsync().ConfigureAwait(false);
            Dictionary<string, object> datos = ConvertirResultado(resultado);
            datos["operacion"] = 38;
            datos["respuesta_recibida"] = !string.IsNullOrEmpty(resultado.RespuestaCruda);

            await ResponderAsync(contexto, 200, datos);
        }

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

            string medio = NormalizarMedio(solicitud.medio_pago);
            if (medio.Length > 0 && !EsMedioTarjeta(medio))
            {
                bool esBilletera = EsMedioBilletera(medio);
                await ResponderAsync(
                    contexto,
                    esBilletera ? 501 : 400,
                    Error(esBilletera
                        ? MensajeBilleteraNoSoportada
                        : "El medio de pago «" + solicitud.medio_pago.Trim() + "» no es válido. " +
                          "Use «TARJETA» o deje el campo vacío."));
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

        private async Task<CobroTarjetaResultado> EjecutarAnulacionAsync(string referencia)
        {
            return await _pinpad.AnularAsync(referencia).ConfigureAwait(false);
        }

        internal async Task<Dictionary<string, object>> EjecutarOrdenBridgeAsync(
            Dictionary<string, object> orden)
        {
            object idValor;
            object operacionValor;
            if (!orden.TryGetValue("id", out idValor) ||
                !orden.TryGetValue("operacion", out operacionValor) ||
                !Guid.TryParse(Convert.ToString(idValor, CultureInfo.InvariantCulture), out Guid clave) ||
                clave == Guid.Empty)
            {
                throw new InvalidOperationException("La orden del servidor no tiene un id válido.");
            }

            string operacion = Convert.ToString(operacionValor, CultureInfo.InvariantCulture);
            CobroTarjetaResultado resultado;
            switch (operacion)
            {
                case "cobrar":
                    object montoValor;
                    decimal monto;
                    if (!orden.TryGetValue("monto", out montoValor) ||
                        !decimal.TryParse(
                            Convert.ToString(montoValor, CultureInfo.InvariantCulture),
                            NumberStyles.Number,
                            CultureInfo.InvariantCulture,
                            out monto) ||
                        monto <= 0)
                    {
                        throw new InvalidOperationException("La orden de cobro tiene un monto inválido.");
                    }

                    object medioValor;
                    string medioOrden = orden.TryGetValue("medio_pago", out medioValor)
                        ? NormalizarMedio(Convert.ToString(medioValor, CultureInfo.InvariantCulture))
                        : string.Empty;
                    if (medioOrden.Length > 0 && !EsMedioTarjeta(medioOrden))
                    {
                        throw new InvalidOperationException(EsMedioBilletera(medioOrden)
                            ? MensajeBilleteraNoSoportada
                            : "La orden de cobro tiene un medio de pago inválido.");
                    }

                    CobroEnCache cobro = ObtenerOCrearCobro(clave, monto);
                    if (cobro.Monto != monto)
                        throw new InvalidOperationException(
                            "La clave de idempotencia ya se usó con otro monto.");
                    resultado = await cobro.Resultado.ConfigureAwait(false);
                    return ConvertirResultado(resultado);

                case "anular":
                    object referenciaValor;
                    if (!orden.TryGetValue("referencia", out referenciaValor))
                        throw new InvalidOperationException(
                            "La orden de anulación no tiene referencia.");
                    string referencia = Convert.ToString(
                        referenciaValor, CultureInfo.InvariantCulture).Trim();
                    if (referencia.Length == 0 || referencia.Length > 32)
                        throw new InvalidOperationException(
                            "La referencia de anulación es inválida.");

                    AnulacionEnCache anulacion = ObtenerOCrearAnulacion(clave, referencia);
                    if (!string.Equals(
                        anulacion.Referencia, referencia, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "La clave de idempotencia ya se usó con otra referencia.");
                    }
                    resultado = await anulacion.Resultado.ConfigureAwait(false);
                    return ConvertirResultado(resultado);

                case "probar":
                    resultado = await _pinpad.ProbarEcoAsync().ConfigureAwait(false);
                    bool respuestaRecibida = !string.IsNullOrEmpty(resultado.RespuestaCruda);
                    return new Dictionary<string, object>
                    {
                        { "comunicacion_ok", resultado.Aprobado && respuestaRecibida },
                        { "respuesta_recibida", respuestaRecibida },
                        { "mensaje_error", resultado.MensajeError },
                    };

                case "imprimir":
                    return await ImprimirDesdeBridgeAsync(clave, orden).ConfigureAwait(false);

                case "estado_impresora":
                    return DiagnosticoDeImpresora();

                default:
                    throw new InvalidOperationException("La operación del Pinpad no está permitida.");
            }
        }

        /// <summary>
        /// Imprime el comprobante que llegó como orden del agente remoto.
        ///
        /// El servidor ya no comparte máquina con la térmica, así que el ticket
        /// tiene que viajar: cuando el backend corre remoto, su
        /// <c>127.0.0.1:5005</c> es su propia máquina y no la caja. La orden
        /// trae el mismo JSON que el endpoint local —se aplana sobre la orden,
        /// con <c>id</c> y <c>operacion</c> al lado—, así que se rearma con el
        /// mismo contrato y pasa por la misma validación y la misma caché de
        /// idempotencia. Reimprimir dos veces por un reintento es tan caro como
        /// cobrar dos veces.
        /// </summary>
        private async Task<Dictionary<string, object>> ImprimirDesdeBridgeAsync(
            Guid clave, Dictionary<string, object> orden)
        {
            ImpresionComprobanteRequest solicitud = LeerSolicitudDeImpresion(orden);

            TrabajoDeImpresion trabajo;
            string problema = PrepararImpresion(solicitud, out trabajo);
            if (problema != null)
                throw new InvalidOperationException(problema);

            ImpresionEnCache impresion;
            bool impresoAhora = false;

            if (!BuscarImpresion(clave, out impresion))
            {
                // Igual que en el camino HTTP: la impresora se resuelve ANTES de
                // memorizar la clave, para que un fallo no la deje envenenada.
                string impresora = ImpresionService.Impresora();
                impresion = ObtenerOCrearImpresion(
                    clave, impresora, trabajo,
                    solicitud.emisor.AModelo(), solicitud.contexto.AModelo(),
                    solicitud.copia, out impresoAhora);
            }

            if (!impresion.Coincide(trabajo, solicitud.copia))
            {
                throw new InvalidOperationException(
                    "La clave de idempotencia ya se utilizó para otro comprobante.");
            }

            ResultadoImpresion resultado =
                await EsperarImpresionAsync(clave, impresion).ConfigureAwait(false);
            return RespuestaDeImpresion(impresion, resultado, impresoAhora);
        }

        /// <summary>
        /// Rearma el comprobante desde la orden del agente.
        ///
        /// La orden llega como diccionario —el JSON ya deserializado— con el
        /// comprobante aplanado al mismo nivel que <c>id</c> y <c>operacion</c>.
        /// Se vuelve a serializar para deserializarla con el contrato de
        /// siempre, en vez de traducir campo por campo a mano: son más de veinte
        /// campos y cada uno que se olvide es un ticket que sale en blanco.
        /// </summary>
        private static ImpresionComprobanteRequest LeerSolicitudDeImpresion(
            Dictionary<string, object> orden)
        {
            string json = new JavaScriptSerializer().Serialize(orden);
            try
            {
                return new JavaScriptSerializer().Deserialize<ImpresionComprobanteRequest>(json);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    "La orden de impresión no trae un comprobante válido.", ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "La orden de impresión no trae un comprobante válido.", ex);
            }
        }

        // ----------------------------------------------------------- anulación

        /// <summary>
        /// Deshace en el equipo un cobro ya aprobado, citando la referencia de su
        /// voucher.
        ///
        /// Existe porque un cobro en curso no se puede abortar: la llamada nativa
        /// de PCL es bloqueante y la DLL no expone cancelación alguna (ver
        /// <c>PinpadService.ConEspera</c>). Lo único que este servicio puede
        /// deshacer es un cobro que YA se aprobó, y eso es exactamente esto:
        /// manda al aparato una operación de anulación con la referencia del
        /// voucher, y con eso el importe vuelve a la tarjeta del cliente.
        ///
        /// Se anula por <b>referencia</b> —el «REF» impreso en el papel— y no por
        /// autorización: el equipo busca la operación por referencia, y con la
        /// autorización no encontraría qué anular.
        ///
        /// La <c>Idempotency-Key</c> impide anular dos veces si la respuesta se
        /// pierde y el backend reintenta, y una clave reutilizada con otra
        /// referencia se rechaza con 409 en vez de anular la operación
        /// equivocada. Es el mismo trato que el cobro, porque el error es igual
        /// de caro: un doble cobro y una doble anulación son los dos formas de
        /// dejar el arqueo mintiendo.
        ///
        /// Sólo procede dentro del lote abierto: después del cierre del día el
        /// equipo la rechaza y la devolución se gestiona con el banco.
        /// </summary>
        private async Task AtenderAnulacionAsync(HttpListenerContext contexto)
        {
            Guid clave;
            if (!ClaveIdempotencia(contexto, out clave))
            {
                await ResponderAsync(contexto, 400, Error("Se requiere un Idempotency-Key UUID válido."));
                return;
            }

            if (!CuerpoDentroDelLimite(contexto, MaximoCuerpoCortoBytes))
            {
                await ResponderAsync(contexto, 413, Error("El cuerpo de la solicitud excede el tamaño permitido."));
                return;
            }

            AnulacionPinpadRequest solicitud = await LeerCuerpoAsync<AnulacionPinpadRequest>(contexto)
                .ConfigureAwait(false);

            if (solicitud == null)
            {
                await ResponderAsync(contexto, 400, Error("El cuerpo JSON no es válido."));
                return;
            }

            string referencia = (solicitud.referencia ?? string.Empty).Trim();
            if (referencia.Length == 0)
            {
                await ResponderAsync(
                    contexto, 400, Error("La referencia del cobro a anular es obligatoria."));
                return;
            }

            AnulacionEnCache anulacion = ObtenerOCrearAnulacion(clave, referencia);
            if (!string.Equals(anulacion.Referencia, referencia, StringComparison.Ordinal))
            {
                await ResponderAsync(
                    contexto, 409, Error("La clave de idempotencia ya se utilizó con otra referencia."));
                return;
            }

            CobroTarjetaResultado resultado = await anulacion.Resultado.ConfigureAwait(false);
            await ResponderAsync(contexto, 200, ConvertirResultado(resultado));
        }

        private AnulacionEnCache ObtenerOCrearAnulacion(Guid clave, string referencia)
        {
            lock (_candado)
            {
                DateTimeOffset ahora = DateTimeOffset.UtcNow;
                PurgarVencidas(ahora);

                AnulacionEnCache existente;
                if (_anulaciones.TryGetValue(clave, out existente))
                    return existente;

                var creado = new AnulacionEnCache
                {
                    Referencia = referencia,
                    CreadoUtc = ahora,
                    Resultado = EjecutarAnulacionAsync(referencia),
                };
                _anulaciones.Add(clave, creado);
                return creado;
            }
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

            TrabajoDeImpresion trabajo;
            string problema = PrepararImpresion(solicitud, out trabajo);
            if (problema != null)
            {
                await ResponderAsync(contexto, 400, Error(problema));
                return;
            }

            // Se valida con el propio predicado del modelo en vez de repetir la
            // regla acá: el ticket sin RUC o sin razón social no es un ticket.
            EmisorImpresion emisor = solicitud.emisor.AModelo();

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
                    clave, impresora, trabajo, emisor, contextoImpresion, solicitud.copia,
                    out impresoAhora);
            }

            if (!impresion.Coincide(trabajo, solicitud.copia))
            {
                await ResponderAsync(
                    contexto, 409,
                    Error("La clave de idempotencia ya se utilizó para otro comprobante."));
                return;
            }

            await ResponderImpresionAsync(contexto, clave, impresion, impresoAhora);
        }

        /// <summary>
        /// Espera el resultado memorizado y responde. Si la impresión falló, se
        /// olvida la clave para que el reintento pueda imprimir otra vez.
        /// </summary>
        private async Task ResponderImpresionAsync(
            HttpListenerContext contexto, Guid clave, ImpresionEnCache impresion, bool impresoAhora)
        {
            ResultadoImpresion resultado =
                await EsperarImpresionAsync(clave, impresion).ConfigureAwait(false);
            await ResponderAsync(
                contexto, 200, RespuestaDeImpresion(impresion, resultado, impresoAhora));
        }

        /// <summary>
        /// Espera la impresión y, si falló, suelta la clave.
        ///
        /// A diferencia del cobro, acá SÓLO se memoriza lo que salió bien: un
        /// cobro fallido no se reintenta con la misma clave —reintentaría la
        /// venta—, pero una impresión fallida sí, porque el cajero prende la
        /// impresora, carga papel y vuelve a pedir el ticket. Memorizar el fallo
        /// dejaría la clave envenenada 24 horas.
        /// </summary>
        private async Task<ResultadoImpresion> EsperarImpresionAsync(
            Guid clave, ImpresionEnCache impresion)
        {
            try
            {
                return await impresion.Resultado.ConfigureAwait(false);
            }
            catch
            {
                OlvidarImpresion(clave, impresion);
                throw;
            }
        }

        /// <summary>Lo que el backend espera de un ticket ya entregado a la cola.</summary>
        private static Dictionary<string, object> RespuestaDeImpresion(
            ImpresionEnCache impresion, ResultadoImpresion resultado, bool impresoAhora)
        {
            return new Dictionary<string, object>
            {
                { "impreso", true },
                { "ya_impreso", !impresoAhora },
                { "impresora", resultado.Impresora },
                { "serie_numero", impresion.SerieNumero },
                { "copia", impresion.Copia },
            };
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
            Guid clave, string impresora, TrabajoDeImpresion trabajo, EmisorImpresion emisor,
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
                    Documento = trabajo.Documento,
                    SerieNumero = trabajo.SerieNumero,
                    Copia = copia,
                    CreadoUtc = DateTimeOffset.UtcNow,
                    Resultado = EjecutarImpresionAsync(impresora, trabajo, emisor, contexto, copia),
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
            string impresora, TrabajoDeImpresion trabajo, EmisorImpresion emisor,
            ContextoImpresion contexto, bool copia)
        {
            return Task.Run(() =>
            {
                // La impresora ya viene resuelta: se le pasa para que el
                // servicio no vuelva a resolverla y el nombre informado sea
                // exactamente el que recibió el trabajo.
                trabajo.Imprimir(impresora, emisor, contexto, copia);
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
        /// Comprueba el sobre y decide qué hay que imprimir.
        ///
        /// Devuelve el motivo por el que no se puede imprimir, o <c>null</c> con
        /// <paramref name="trabajo"/> ya armado. Existe aparte porque el papel
        /// llega por dos caminos —el HTTP local y la orden del agente remoto— y
        /// las dos puertas tienen que exigir exactamente lo mismo; si
        /// divergieran, un sobre inválido entraría por la que se quedó atrás.
        ///
        /// Desde que la constancia de anulación viaja por este mismo endpoint,
        /// el sobre puede traer dos cosas distintas. Se resuelve acá, una sola
        /// vez, y el resto del camino ya no vuelve a preguntar de qué papel se
        /// trata: repetir esa pregunta en cada puerta es cómo se cuela una
        /// constancia por el camino del ticket y termina en un 400 que no
        /// explica nada.
        /// </summary>
        private static string PrepararImpresion(
            ImpresionComprobanteRequest solicitud, out TrabajoDeImpresion trabajo)
        {
            trabajo = null;

            if (solicitud == null)
                return "El cuerpo JSON no es válido.";
            if (solicitud.emisor == null || solicitud.contexto == null)
                return "Se requieren 'emisor' y 'contexto'.";
            if (!solicitud.emisor.AModelo().PuedeEmitir())
                return "El emisor debe traer 'ruc' y 'razon_social'.";

            if (solicitud.EsConstanciaDeAnulacion())
            {
                ConstanciaAnulacionImpresion constancia = solicitud.AConstancia();
                if (constancia == null || !constancia.PuedeImprimir())
                    return "La constancia debe traer 'anulacion.comprobante'.";

                trabajo = TrabajoDeImpresion.DeConstancia(constancia);
                return null;
            }

            if (solicitud.venta == null)
                return "Se requieren 'venta', 'emisor' y 'contexto'.";
            if (string.IsNullOrWhiteSpace(solicitud.venta.serie_numero))
                return "La venta debe traer 'serie_numero'.";

            ComprobanteImpresion venta = solicitud.AModelo();
            if (venta == null)
                return "La venta debe traer al menos una línea válida.";

            trabajo = TrabajoDeImpresion.DeVenta(venta);
            return null;
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

            await ResponderAsync(contexto, 200, DiagnosticoDeImpresora());
        }

        /// <summary>
        /// Lo que este equipo sabe de su impresora. La comparten el HTTP local y
        /// el agente remoto: el cajero tiene que leer lo mismo por las dos vías.
        /// </summary>
        private static Dictionary<string, object> DiagnosticoDeImpresora()
        {
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

            return new Dictionary<string, object>
            {
                { "disponible", impresora != null },
                { "impresora", impresora },
                { "detalle", detalle },
                { "columnas", EscPos.Columnas },
                { "pagina_codigos", EscPos.PaginaCodigos },
            };
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
            foreach (KeyValuePair<Guid, AnulacionEnCache> par in _anulaciones)
            {
                if (par.Value.Resultado.IsCompleted &&
                    ahora - par.Value.CreadoUtc >= VidaClaveIdempotencia)
                    vencidas.Add(par.Key);
            }
            foreach (Guid vencida in vencidas)
                _anulaciones.Remove(vencida);

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

        private const string MensajeBilleteraNoSoportada =
            "El cobro con billetera (Yape/Plin) no se puede pedir desde el sistema: la trama " +
            "del pinpad sólo lleva la operación y el monto, así que el medio de pago lo elige " +
            "el cliente en la pantalla del equipo. Si la billetera está habilitada por Niubiz, " +
            "cobre como TARJETA y elija Yape/Plin en el pinpad; si ese menú no aparece, la " +
            "habilitación del comercio/terminal está pendiente con Niubiz.";

        private static string NormalizarMedio(string medio) =>
            (medio ?? string.Empty).Trim().ToUpperInvariant();

        /// <summary>
        /// Todo lo que empiece con «TARJETA» se acepta (TARJETA, TARJETA_DEBITO,
        /// TARJETA_CREDITO…): el equipo decide débito o crédito, este módulo no.
        /// </summary>
        private static bool EsMedioTarjeta(string medio) =>
            medio.StartsWith("TARJETA", StringComparison.Ordinal);

        private static bool EsMedioBilletera(string medio)
        {
            switch (medio)
            {
                case "BILLETERA":
                case "YAPE":
                case "PLIN":
                case "WALLET":
                    return true;
                default:
                    return false;
            }
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
                { "estado_resultado", resultado.EstadoResultado },
                { "mensaje_equipo", resultado.MensajeEquipo },
                { "marca_equipo", resultado.MarcaEquipo },
                { "tipo_cuenta", resultado.TipoCuenta },
                { "producto_equipo", resultado.ProductoEquipo },
                { "billetera", resultado.Billetera },
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

        /// <summary>
        /// Una anulación ya pedida, guardada por su clave para poder responder lo
        /// mismo si el backend reintenta. <see cref="Referencia"/> se escribe una
        /// sola vez, antes de publicar el objeto en el diccionario, así que
        /// leerlo desde fuera del candado es seguro.
        /// </summary>
        private sealed class AnulacionEnCache
        {
            public string Referencia { get; set; }
            public DateTimeOffset CreadoUtc { get; set; }
            public Task<CobroTarjetaResultado> Resultado { get; set; }
        }

        private sealed class ImpresionEnCache
        {
            /// <summary>Qué papel es: un ticket o una constancia de anulación.</summary>
            public string Documento { get; set; }
            public string SerieNumero { get; set; }
            public bool Copia { get; set; }
            public DateTimeOffset CreadoUtc { get; set; }
            public Task<ResultadoImpresion> Resultado { get; set; }

            /// <summary>Mismo comprobante y mismo tipo de papel, o no es un reintento.</summary>
            public bool Coincide(TrabajoDeImpresion trabajo, bool copia)
            {
                return Copia == copia &&
                       string.Equals(
                           Documento ?? string.Empty,
                           trabajo.Documento ?? string.Empty,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           SerieNumero ?? string.Empty,
                           trabajo.SerieNumero ?? string.Empty,
                           StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// El papel que se dedujo del sobre, ya armado.
        ///
        /// El endpoint de impresión atiende dos formatos desde que existe la
        /// constancia de anulación: el ticket de venta y el comprobante de la
        /// devolución. Los dos comparten la caché de idempotencia y la respuesta
        /// —el backend espera el mismo sobre de vuelta—, así que lo que cambia
        /// es sólo el armado y el envío. Envolver esa diferencia acá deja un
        /// único «si es constancia» en todo el host.
        /// </summary>
        private sealed class TrabajoDeImpresion
        {
            private TrabajoDeImpresion() { }

            /// <summary>Nulo cuando el papel es una constancia.</summary>
            public ComprobanteImpresion Venta { get; private set; }

            /// <summary>Nula cuando el papel es un ticket.</summary>
            public ConstanciaAnulacionImpresion Constancia { get; private set; }

            /// <summary>
            /// Con qué se identifica el papel en la respuesta y en la caché.
            ///
            /// Para el ticket es la serie del comprobante; para la constancia, el
            /// número del comprobante ANULADO, que es lo único que el backend
            /// puede cotejar y lo que va impreso.
            /// </summary>
            public string SerieNumero
            {
                get
                {
                    if (Constancia != null) return Constancia.Comprobante ?? string.Empty;
                    return Venta != null ? Venta.SerieNumero ?? string.Empty : string.Empty;
                }
            }

            /// <summary>
            /// Nombre del papel. Se compara junto con la serie para que una
            /// constancia y un ticket del mismo comprobante no se confundan si
            /// alguna vez comparten clave de idempotencia.
            /// </summary>
            public string Documento
            {
                get
                {
                    return Constancia != null
                        ? ImpresionComprobanteRequest.DocumentoConstanciaAnulacion
                        : "VENTA";
                }
            }

            public static TrabajoDeImpresion DeVenta(ComprobanteImpresion venta)
            {
                return new TrabajoDeImpresion { Venta = venta };
            }

            public static TrabajoDeImpresion DeConstancia(ConstanciaAnulacionImpresion constancia)
            {
                return new TrabajoDeImpresion { Constancia = constancia };
            }

            public void Imprimir(
                string impresora, EmisorImpresion emisor, ContextoImpresion contexto, bool copia)
            {
                var servicio = new ImpresionService(impresora);
                if (Constancia != null)
                    servicio.ImprimirConstancia(Constancia, emisor, contexto, copia);
                else
                    servicio.Imprimir(Venta, emisor, contexto, copia);
            }
        }

        private sealed class ResultadoImpresion
        {
            public string Impresora { get; set; }
        }
    }
}
