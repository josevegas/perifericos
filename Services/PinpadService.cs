using PuntoVenta.Infrastructure.Pinpad;
using PuntoVenta.Models.Pinpad;
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PuntoVenta.Services
{
    /// <summary>
    /// Cobro con tarjeta por el pinpad de Niubiz (equipo Ingenico, protocolo PCL).
    ///
    /// Portado del programa legacy (frmPagar/frmMenu de PuntoVentaSanJose) con la misma
    /// mecánica probada en producción: se arma un mensaje TLV con el monto, se envía
    /// con doTransactionEx a la app de Niubiz que corre DENTRO del equipo (la tarjeta
    /// nunca pasa por esta máquina), y la respuesta trae dos vouchers imprimibles.
    /// Autorización/lote/referencia NO llegan como campos: se extraen del texto del
    /// voucher (ParsearVoucher) — frágil ante cambios de formato de Niubiz, por eso
    /// el voucher crudo siempre se conserva en el resultado para diagnóstico.
    /// </summary>
    public sealed class PinpadService
    {
        // Tags TLV del protocolo con la app de Niubiz (valores del legacy).
        private const int TagPadre = 252;
        private const long TagIdExternalDevice = 14679845;
        private const long TagTransactionId = 14679635;
        private const long TagFecha = 154;            // yyMMdd como entero
        private const long TagHora = 40737;           // HHmmss como entero
        private const long TagOperacion = 220;        // 10 = COMPRA
        private const long TagCashierNumber = 14679851;
        private const long TagMonto = 14679616;       // centavos, formato D12
        private const long TagCuotas = 14679846;
        private const long TagVoucherComercio = 14679811;
        private const long TagVoucherCliente = 14679812;
        private const long TagReferenciaAnular = 14679849;   // "invoice number"
        private const long TagCodigoRespuesta = 14679855;    // "00" aprobado, "05" denegada…
        private const long TagMensajeRespuesta = 14679852;   // texto del equipo/banco
        private const long TagEstadoResultado = 14679810;    // DF FF 02: 0 = OK, 3 = falló

        // Medio con el que se cobró. No están en la trama de salida: llegan en la
        // respuesta, y son la única forma de distinguir una billetera de una
        // tarjeta (una billetera no trae tarjeta pero sí reporta estos datos).
        // La marca viene en la raíz y el resto dentro del contenedor DF FF 34.
        private const long TagDatosDelMedio = 14679860;  // DF FF 34: lote, red, cuenta, producto
        private const long TagTipoCuenta = 14679872;     // DF FF 40: "DEBITO" / "CREDITO"
        private const long TagMarcaEquipo = 14679873;    // DF FF 41: "VISA DEBITO", …
        private const long TagProductoEquipo = 14679876; // DF FF 44: "VISA INTERNACIONAL", …

        private const string RespuestaAprobada = "00";

        private const long OperacionCompra = 10;
        private const long OperacionAnulacion = 2;
        private const long OperacionCierre = 15;
        private const long OperacionEcoTest = 20;
        private const long OperacionInicializar = 38;

        /// <summary>
        /// La trama PCL de este módulo NO lleva el medio de pago: sólo viajan la
        /// operación (COMPRA) y el monto. La elección —tarjeta, Yape, Plin— la
        /// hace el cliente en el menú de la app de Niubiz DENTRO del equipo, y
        /// sólo aparece si el comercio tiene la billetera habilitada por Niubiz.
        /// Por eso no existe «cobrar por Yape» desde acá: se puede LEER con qué se
        /// pagó (ver <see cref="EsBilletera"/>), no imponerlo.
        /// </summary>
        public const bool SoportaIndicacionDeMedioPago = false;

        /// <summary>
        /// Nombres con los que el equipo puede reportar una billetera en la marca
        /// o en el tipo de cuenta. Niubiz no publica el literal del PCL, así que
        /// la comparación es por nombre comercial conocido; si aparece otro texto,
        /// la venta igual se aprueba y queda el valor crudo en
        /// <see cref="CobroTarjetaResultado.MarcaEquipo"/> para ampliar esta lista.
        /// </summary>
        private static readonly string[] NombresDeBilletera =
            { "YAPE", "PLIN", "BIM", "TUNKI", "BILLETERA", "WALLET", "QR" };

        // Byte↔carácter 1:1 — la codificación del protocolo PCL/TLV.
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        // Cuánto se espera al equipo antes de devolverle el control al cajero.
        // `doTransactionEx` es nativa y BLOQUEANTE: si el terminal se cuelga
        // (su "ERROR SISTEMA") nunca retorna, y sin este tope la caja queda
        // inutilizable esperando para siempre.
        private static readonly TimeSpan EsperaCobro = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan EsperaDiagnostico = TimeSpan.FromSeconds(30);

        // Ruta absoluta a propósito: con la relativa el archivo caía en el
        // directorio de trabajo del proceso, que en una tarea programada del
        // Programador de tareas es C:\Windows\System32 — un lugar del que nadie
        // lo va a sacar, y donde el append puede fallar en silencio.
        private static readonly string RutaLog = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "pinpad.log");

        /// <summary>Hay una operación nativa colgada: no se lanza otra encima.</summary>
        private static volatile bool _operacionHuerfana;
        private static readonly SemaphoreSlim CandadoOperacion = new SemaphoreSlim(1, 1);

        public sealed class EquipoUsb
        {
            public string Puerto { get; set; }
            public string Nombre { get; set; }
            public bool Activado { get; set; }
        }

        /// <summary>
        /// Deja el servicio PCL levantado y el equipo pareado si aún no lo están.
        /// Idempotente y a prueba de equipo ausente: se llama al arrancar la
        /// aplicación para que el enlace ya esté vivo cuando el islero llegue a cobrar.
        /// </summary>
        public static Task<bool> AsegurarServicioEnSegundoPlano()
        {
            return Task.Run(() =>
            {
                try
                {
                    var servicio = new PinpadService();
                    if (!servicio.EstaConectado()) servicio.Conectar();

                    // Un equipo recién pareado no siempre aparece en serverStatus
                    // sin volver a levantar la sesión, así que se reintenta.
                    if (servicio.AsegurarEquipoPareado() && !servicio.EstaConectado())
                        servicio.Conectar();

                    bool conectado = servicio.EstaConectado();
                    Log("canal PCL listo=" + conectado);
                    return conectado;
                }
                catch (DllNotFoundException)
                {
                    Log("No se encontró PCLService.dll al iniciar el servicio.");
                    return false;
                }
                catch (Exception ex)
                {
                    Log("Arranque del servicio PCL falló: " + ex.Message);
                    return false;
                }
            });
        }

        /// <summary>
        /// Arranca el servicio PCL (la sesión con el equipo pareado). Idempotente.
        /// </summary>
        public bool Conectar()
        {
            // Bitácora interna de la DLL de Ingenico: escribe SU propio log con
            // la razón real de los fallos de enlace (imprescindible en diagnóstico).
            try { PCLdll.enableDebugLog(true); } catch { }
            bool ok = PCLdll.startPclService();
            Log("startPclService=" + ok);

            // PCL puede aceptar el arranque antes de publicar el canal. Verificar
            // serverStatus también cuando startPclService devuelve true evita
            // enviar la primera transacción durante esa ventana de inicialización.
            bool conectado = EsperarCanal(TimeSpan.FromSeconds(5));
            Log("canal PCL listo=" + conectado);
            return conectado;
        }

        /// <summary>
        /// Espera a que serverStatus reporte el canal listo (arranque del servicio).
        /// </summary>
        private bool EsperarCanal(TimeSpan limite)
        {
            DateTime fin = DateTime.UtcNow + limite;
            while (true)
            {
                if (EstaConectado()) return true;
                if (DateTime.UtcNow >= fin) return false;
                System.Threading.Thread.Sleep(200);
            }
        }

        /// <summary>
        /// Enlace REAL con el equipo: pide el número de serie por el canal PCL.
        /// serverStatus puede decir "conectado" con el canal a medias; esto no.
        /// </summary>
        public string LeerSerieDelEquipo()
        {
            var sn = new StringBuilder(64);
            if (PCLdll.getFullSerialNumber(sn, 64) && sn.Length > 0)
                return sn.ToString();
            return null;
        }

        /// <summary>
        /// Lee la configuración TMS del terminal (servidor, puerto, identificador y
        /// perfil SSL) y sus números de serie.
        /// </summary>
        /// <remarks>
        /// Es de SÓLO LECTURA: no cambia nada en el equipo ni dispara descargas.
        /// El identificador TMS es lo que Niubiz usa para saber qué aplicación y
        /// qué parámetros (medios habilitados, billeteras) le corresponden a este
        /// terminal, así que es el dato que hay que darle al soporte cuando el menú
        /// de Yape/Plin no aparece.
        /// </remarks>
        public Dictionary<string, object> ConsultarParametrosTms()
        {
            var datos = new Dictionary<string, object>();
            datos["conectado"] = EstaConectado();
            datos["serie"] = LeerSerieDelEquipo();

            var equipo = new PCLdll.spmInfo_t();
            if (PCLdll.getTerminalInfo(equipo))
            {
                datos["serie_terminal"] = equipo.dwSerialNumber;
                datos["producto_terminal"] = equipo.dwProductNumber;
            }

            if (!CandadoOperacion.Wait(0))
            {
                datos["tms_error"] = "Hay una operación en curso en el pinpad; reintente cuando termine.";
                return datos;
            }

            try
            {
                var direccion = new StringBuilder(258);
                var puerto = new StringBuilder(6);
                var identificador = new StringBuilder(16);
                var ssl = new PCLdll.tms_ssl_parameter_t();
                var estado = new StringBuilder(8);
                bool ok = PCLdll.tmsReadParam(
                    direccion, 258, puerto, 6, identificador, 16, ssl, estado);

                datos["tms_ok"] = ok;
                datos["tms_estado"] = estado.ToString();
                datos["tms_direccion"] = direccion.ToString();
                datos["tms_puerto"] = puerto.ToString();
                datos["tms_identificador"] = identificador.ToString();
                datos["tms_ssl_actual"] = ssl.current_ssl_profile;
                datos["tms_ssl_cantidad"] = ssl.nb_ssl_profile;

                Log("TMS ok=" + ok + " dir=" + datos["tms_direccion"] + " puerto=" + datos["tms_puerto"]
                    + " id=" + datos["tms_identificador"] + " ssl=" + datos["tms_ssl_actual"]);
            }
            catch (Exception ex)
            {
                datos["tms_error"] = ex.Message;
                Log("TMS ERROR " + ex.Message);
            }
            finally
            {
                CandadoOperacion.Release();
            }

            return datos;
        }

        /// <summary>
        /// Lanza una actualización Telium: el terminal vuelve a bajar aplicación y
        /// parámetros desde su TMS.
        /// </summary>
        /// <remarks>
        /// Es una ESCRITURA sobre el equipo, no un diagnóstico. Mientras corre no
        /// se puede cobrar (se retiene el candado de operación) y si la descarga
        /// se corta el terminal puede quedar sin la aplicación de Niubiz hasta que
        /// el TMS se la vuelva a empujar. Se lanza en segundo plano porque la
        /// llamada nativa puede tardar minutos y no se puede cancelar.
        /// </remarks>
        public Dictionary<string, object> IniciarActualizacionTms()
        {
            var datos = new Dictionary<string, object>();

            if (!EstaConectado())
            {
                datos["iniciada"] = false;
                datos["motivo"] = "El canal PCL no está listo.";
                return datos;
            }

            if (!CandadoOperacion.Wait(0))
            {
                datos["iniciada"] = false;
                datos["motivo"] = "Hay una operación en curso en el pinpad.";
                return datos;
            }

            datos["iniciada"] = true;
            Log("TMS UPDATE solicitado");

            Task.Run(() =>
            {
                try
                {
                    uint resultado = 0;
                    bool ok = PCLdll.doUpdate(ref resultado);
                    Log("TMS UPDATE terminó ok=" + ok + " resultado=" + resultado);
                }
                catch (Exception ex)
                {
                    Log("TMS UPDATE ERROR " + ex.Message);
                }
                finally
                {
                    CandadoOperacion.Release();
                }
            });

            return datos;
        }

        /// <summary>
        /// Drena mensajes espontáneos del equipo (el legacy lo hacía en cada tick;
        /// mantiene sano el canal). Devuelve el mensaje si hubo alguno.
        /// </summary>
        public string BombearMensajes()
        {
            var buffer = new byte[100];
            uint recibidos = 0;
            if (PCLdll.receiveMessage(buffer, 100, ref recibidos) && recibidos > 0)
            {
                string msg = Latin1.GetString(buffer, 0, (int)recibidos);
                Log("MENSAJE del equipo: " + TLVClass.ToHex(msg));
                return msg;
            }
            return null;
        }

        public void Desconectar()
        {
            PCLdll.stopPclService();
        }

        /// <summary>true cuando el servicio reporta enlace vivo con el equipo.</summary>
        public bool EstaConectado()
        {
            var status = new StringBuilder(4);
            if (!PCLdll.serverStatus(status)) return false;
            return status.Length > 0 && status[0] == (char)1;
        }

        /// <summary>Equipos Ingenico enchufados por USB a esta máquina.</summary>
        public List<EquipoUsb> ListarUsb()
        {
            var lista = new List<EquipoUsb>();
            var companions = new PCLdll.PUSB_COMPANION_INFO();
            int size = (248 + 20 + 20 + 2) * 100;
            int entradas = 0;
            PCLdll.getUSBDevices(ref companions, ref size, ref entradas);
            for (int i = 0; i < entradas; i++)
            {
                lista.Add(new EquipoUsb
                {
                    Puerto = companions.USB[i].szPort,
                    Nombre = companions.USB[i].szName,
                    Activado = companions.USB[i].fActivated,
                });
            }
            return lista;
        }

        /// <summary>Parea el equipo USB (una vez por máquina; queda registrado en PCL).</summary>
        public bool ActivarUsb(string puerto)
        {
            return PCLdll.activateUSBDevice(puerto);
        }

        /// <summary>
        /// Parea el primer equipo Ingenico visible que todavía no lo esté.
        /// </summary>
        /// <remarks>
        /// La activación es un paso único por máquina que PCL guarda en su propia
        /// configuración. Sin él el terminal aparece enchufado en ListarUsb() pero
        /// serverStatus no reporta canal y NINGUNA transacción responde: eso es
        /// exactamente lo que se veía en una caja recién instalada, y no lo hacía
        /// nadie. Si ya hay un equipo activado no se toca nada.
        /// </remarks>
        public bool AsegurarEquipoPareado()
        {
            try
            {
                foreach (EquipoUsb equipo in ListarUsb())
                {
                    if (equipo.Activado || string.IsNullOrEmpty(equipo.Puerto)) continue;
                    bool ok = ActivarUsb(equipo.Puerto);
                    Log("activateUSBDevice(" + equipo.Puerto + ")=" + ok);
                    if (ok) return true;
                }
            }
            catch (DllNotFoundException)
            {
                // Caja sin pinpad: no hay nada que parear.
            }
            catch (Exception ex)
            {
                Log("Pareo del pinpad falló: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Envía el cobro al pinpad y ESPERA a que el cliente pase/acerca la tarjeta
        /// en el equipo. Bloquea hasta la respuesta — por eso corre en Task.Run.
        /// </summary>
        public Task<CobroTarjetaResultado> CobrarAsync(decimal monto)
        {
            decimal montoRedondeado = NormalizarMonto(monto);

            if (!EstaConectado() && !Conectar())
            {
                return Task.FromResult(new CobroTarjetaResultado
                {
                    MensajeError = "No se pudo establecer conexión con el pinpad.",
                });
            }

            return ConEspera(OperacionCompra, montoRedondeado, null, EsperaCobro);
        }

        /// <summary>
        /// Ejecuta la operación sin quedar preso del equipo: si no responde en
        /// el plazo, devuelve el control con un resultado NO aprobado. La llamada
        /// nativa sigue su curso en el pool (no se puede cancelar), por eso queda
        /// marcada como huérfana y se impide lanzar otra hasta reiniciar la aplicación.
        /// </summary>
        private async Task<CobroTarjetaResultado> ConEspera(
            long operacion, decimal? monto, string referencia, TimeSpan espera)
        {
            if (_operacionHuerfana)
            {
                return new CobroTarjetaResultado
                {
                    MensajeError =
                        "Hay una operación anterior que el equipo nunca respondió.\n\n" +
                        "Apague y encienda el pinpad, y reinicie la aplicación antes de volver a cobrar.",
                };
            }

            if (!await CandadoOperacion.WaitAsync(0))
            {
                return new CobroTarjetaResultado
                {
                    MensajeError = "Ya hay una operación en curso con el pinpad. Espere a que termine antes de iniciar otra.",
                };
            }

            Task<CobroTarjetaResultado> operacionEnCurso;
            try
            {
                operacionEnCurso = Task.Run(() => EjecutarOperacion(operacion, monto, referencia));
            }
            catch
            {
                CandadoOperacion.Release();
                throw;
            }

            await Task.WhenAny(operacionEnCurso, Task.Delay(espera));
            if (operacionEnCurso.IsCompleted)
            {
                try
                {
                    return await operacionEnCurso;
                }
                finally
                {
                    CandadoOperacion.Release();
                }
            }

            _operacionHuerfana = true;
            Log("TIMEOUT op=" + operacion + " tras " + espera.TotalSeconds + "s");
            // La llamada nativa no se puede cancelar: conservar el candado hasta
            // que termine evita enviar otra operación al mismo canal PCL.
            _ = operacionEnCurso.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Log("Operación PCL huérfana terminó con error: " + t.Exception.GetBaseException().Message);
                if (operacion != OperacionCompra && operacion != OperacionAnulacion)
                    _operacionHuerfana = false;
                else
                    Log("La operación monetaria terminó después del timeout; se requiere reiniciar y conciliar antes de volver a cobrar.");
                CandadoOperacion.Release();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return new CobroTarjetaResultado
            {
                MensajeError =
                    "El equipo no respondió en " + (int)espera.TotalSeconds + " segundos.\n\n" +
                    "Verifique en la pantalla del pinpad si la operación se completó ANTES de " +
                    "reintentar: apáguelo y enciéndalo, y consulte el último voucher.",
            };
        }

        /// <summary>
        /// Anula un cobro ANTERIOR por su referencia (el "REF" del voucher).
        ///
        /// Solo procede sobre operaciones del LOTE ABIERTO: una vez hecho el
        /// cierre del día el equipo la rechaza y hay que gestionar la devolución
        /// con Niubiz. El equipo puede pedir la tarjeta y/o clave de supervisor.
        /// </summary>
        public Task<CobroTarjetaResultado> AnularAsync(string referencia)
        {
            if (string.IsNullOrWhiteSpace(referencia))
                throw new ArgumentException("La referencia es obligatoria.", nameof(referencia));

            return ConEspera(OperacionAnulacion, null, referencia.Trim(), EsperaCobro);
        }

        /// <summary>Prueba de comunicación con la app de pago (no cobra nada).</summary>
        public async Task<CobroTarjetaResultado> ProbarEcoAsync()
        {
            if (!EstaConectado() && !Conectar())
            {
                if (!AsegurarEquipoPareado() || !Conectar())
                {
                    return new CobroTarjetaResultado
                    {
                        MensajeError =
                            "No se pudo establecer el canal PCL con el pinpad. " +
                            "Verifique el USB, el pareo del equipo y el servicio PCL.",
                    };
                }
            }

            return await ConEspera(
                OperacionEcoTest, null, null, EsperaDiagnostico).ConfigureAwait(false);
        }

        /// <summary>Inicialización del terminal con el host de Niubiz (descarga de parámetros).</summary>
        public async Task<CobroTarjetaResultado> InicializarAsync()
        {
            if (!EstaConectado() && !Conectar())
            {
                return new CobroTarjetaResultado
                {
                    MensajeError = "No se pudo establecer el canal PCL con el pinpad.",
                };
            }

            return await ConEspera(
                OperacionInicializar, null, null, EsperaCobro).ConfigureAwait(false);
        }

        /// <summary>Cierre de lote del terminal.</summary>
        public Task<CobroTarjetaResultado> CierreAsync() =>
            ConEspera(OperacionCierre, null, null, EsperaCobro);

        private CobroTarjetaResultado EjecutarOperacion(
            long operacion, decimal? monto, string referenciaAnular = null)
        {
            var resultado = new CobroTarjetaResultado();

            IntPtr transIn = PCLdll.create_itransactionin();
            IntPtr transOut = PCLdll.create_itransactionout();
            if (transIn == IntPtr.Zero || transOut == IntPtr.Zero)
            {
                resultado.MensajeError = "No se pudo crear la transacción PCL.";
                return resultado;
            }

            string trama = ArmarTrama(operacion, monto, referenciaAnular);
            Log("ENVÍO op=" + operacion + " trama=" + TLVClass.ToHex(trama));
            // OJO: la trama TLV trae bytes ALTOS (el tag padre es 0xFC). La
            // conversión debe ser byte↔carácter 1 a 1 (Latin-1); con ASCII todo
            // byte >127 se vuelve '?' y el equipo rechaza la transacción — el
            // legacy lograba lo mismo quedándose con el byte bajo del UTF-16.
            byte[] entrada = Latin1.GetBytes(trama);
            var salida = new byte[16000];
            int tamanoSalida = salida.Length;

            bool ok = PCLdll.doTransactionEx(
                transIn, transOut, 0, entrada, entrada.Length, salida, ref tamanoSalida);
            Log("RESPUESTA ok=" + ok + " bytes=" + tamanoSalida);

            if (!ok || tamanoSalida <= 0 || tamanoSalida >= salida.Length)
            {
                resultado.MensajeError =
                    "El pinpad no respondió la transacción (doTransactionEx=" + ok +
                    ", bytes=" + tamanoSalida + ").";
                return resultado;
            }

            string respuesta = Latin1.GetString(salida, 0, tamanoSalida);
            resultado.RespuestaCruda = TLVClass.ToHex(respuesta);
            Log("RESPUESTA hex=" + resultado.RespuestaCruda);

            // Diagnóstico (eco/inicializar/cierre): no hay voucher, pero el
            // equipo sí reporta código y texto. doTransactionEx=true sólo
            // significa "contestó": la op 22 respondía "TRANSACCION FALLIDA"
            // con código 03 y quedaba reportada como aprobada.
            if (operacion != OperacionCompra && operacion != OperacionAnulacion)
            {
                try
                {
                    var tlvDiagnostico = new TLVClass(respuesta);
                    XElement raizDiagnostico = tlvDiagnostico.GetParentElement();
                    resultado.CodigoRespuesta =
                        LeerTexto(tlvDiagnostico, raizDiagnostico, TagCodigoRespuesta);
                    resultado.MensajeEquipo =
                        LeerTexto(tlvDiagnostico, raizDiagnostico, TagMensajeRespuesta);
                    resultado.EstadoResultado =
                        LeerEstado(tlvDiagnostico, raizDiagnostico, TagEstadoResultado);
                }
                catch (Exception ex)
                {
                    // Trama inesperada: el equipo contestó, pero no se pudo leer.
                    Log("DIAGNÓSTICO op=" + operacion + " no se pudo interpretar: " + ex.Message);
                }

                // DF FF 02 es el veredicto del equipo para estas operaciones: 0 es
                // lo único que significa "salió bien". Si no vino, se cae al código
                // de respuesta clásico ("00").
                resultado.Aprobado = resultado.EstadoResultado.HasValue
                    ? resultado.EstadoResultado.Value == 0
                    : resultado.CodigoRespuesta == RespuestaAprobada;
                Log("DIAGNÓSTICO op=" + operacion + " estado=" + resultado.EstadoResultado +
                    " codigo=" + resultado.CodigoRespuesta + " mensaje=" + resultado.MensajeEquipo +
                    " aprobado=" + resultado.Aprobado);
                return resultado;
            }

            var tlvRespuesta = new TLVClass(respuesta);
            XElement raiz = tlvRespuesta.GetParentElement();
            XElement voucherComercio = tlvRespuesta.GetTLVElement(raiz, TagVoucherComercio);
            XElement voucherCliente = tlvRespuesta.GetTLVElement(raiz, TagVoucherCliente);

            if (voucherComercio != null)
                resultado.VoucherComercio = tlvRespuesta.GetTLVValueAscii(voucherComercio);
            if (voucherCliente != null)
                resultado.VoucherCliente = tlvRespuesta.GetTLVValueAscii(voucherCliente);

            // El equipo SIEMPRE dice por qué: código ("00" aprobado, "05" denegada
            // por el banco…) y texto. Sin esto el cajero solo veía "no se realizó"
            // y no sabía si insistir, pedir otra tarjeta o cobrar en efectivo.
            resultado.CodigoRespuesta = LeerTexto(tlvRespuesta, raiz, TagCodigoRespuesta);
            resultado.MensajeEquipo = LeerTexto(tlvRespuesta, raiz, TagMensajeRespuesta);
            Log("RESPUESTA codigo=" + resultado.CodigoRespuesta + " mensaje=" + resultado.MensajeEquipo);

            // Con qué se cobró: el protocolo no lo da como campo, pero el equipo
            // sí reporta marca, cuenta y producto. Sin esto una venta con
            // billetera quedaba indistinguible de una con tarjeta.
            resultado.MarcaEquipo = LeerTexto(tlvRespuesta, raiz, TagMarcaEquipo);
            resultado.TipoCuenta = LeerTextoAnidado(
                tlvRespuesta, raiz, TagDatosDelMedio, TagTipoCuenta);
            resultado.ProductoEquipo = LeerTextoAnidado(
                tlvRespuesta, raiz, TagDatosDelMedio, TagProductoEquipo);
            resultado.Billetera = EsBilletera(
                resultado.MarcaEquipo, resultado.TipoCuenta, resultado.ProductoEquipo);
            Log("MEDIO marca=" + resultado.MarcaEquipo + " cuenta=" + resultado.TipoCuenta +
                " producto=" + resultado.ProductoEquipo + " billetera=" + resultado.Billetera);

            // Igual que el legacy: sin voucher no hubo operación (rechazada o
            // cancelada en el equipo). En anulación suele significar que la
            // referencia no existe en el lote abierto — el caso más común es
            // intentar anular algo de un día ya cerrado.
            if (voucherCliente == null || resultado.CodigoRespuesta != RespuestaAprobada)
            {
                resultado.MensajeError = DescribirRechazo(resultado, operacion);
                return resultado;
            }

            ParsearVoucher(resultado.VoucherCliente ?? resultado.VoucherComercio, resultado);
            resultado.Aprobado = true;
            return resultado;
        }

        /// <summary>Valor de un tag como texto, o null si no vino.</summary>
        private static string LeerTexto(TLVClass tlv, XElement raiz, long tag)
        {
            XElement elemento = tlv.GetTLVElement(raiz, tag);
            if (elemento == null) return null;
            string valor = tlv.GetTLVValueAscii(elemento);
            return string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        }

        /// <summary>
        /// Estado de la operación (DF FF 02), que llega como entero de 4 bytes
        /// big-endian: "03000000" es 3 (falló) y "00000000" es 0 (salió bien).
        /// El estado útil es el byte más significativo, o sea el primero del hex.
        /// </summary>
        private static int? LeerEstado(TLVClass tlv, XElement raiz, long tag)
        {
            XElement elemento = tlv.GetTLVElement(raiz, tag);
            if (elemento == null) return null;

            string hex = (tlv.GetTLVValue(elemento) ?? string.Empty).Replace(" ", string.Empty);
            if (hex.Length < 2) return null;

            int valor;
            if (!int.TryParse(
                    hex.Substring(0, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out valor))
                return null;

            return valor;
        }

        /// <summary>
        /// Lee un tag que el equipo manda DENTRO de un contenedor (DF FF 34):
        /// el parser de TLV no aplana a los hijos, así que hay que bajar al nodo
        /// padre antes de buscarlo.
        /// </summary>
        private static string LeerTextoAnidado(
            TLVClass tlv, XElement raiz, long contenedor, long tag)
        {
            XElement padre = tlv.GetTLVElement(raiz, contenedor);
            return padre == null ? null : LeerTexto(tlv, padre, tag);
        }

        /// <summary>
        /// Traduce el rechazo a algo que el cajero pueda accionar: si el banco
        /// denegó, insistir no sirve — hay que pedir otra tarjeta o cobrar de otra
        /// forma. Si fue cancelación en el equipo, sí se puede reintentar.
        /// </summary>
        private static string DescribirRechazo(CobroTarjetaResultado r, long operacion)
        {
            string detalle = r.MensajeEquipo;
            if (!string.IsNullOrEmpty(r.CodigoRespuesta) && r.CodigoRespuesta != RespuestaAprobada)
                detalle = (detalle ?? "Rechazada") + " (código " + r.CodigoRespuesta + ")";

            if (operacion == OperacionAnulacion)
            {
                return (detalle ?? "El equipo no anuló la operación.") +
                       "\n\nVerifique la referencia: solo se anulan cobros del lote AÚN NO CERRADO.";
            }

            if (detalle == null)
                return r.CodigoRespuesta == null
                    ? "El pinpad no confirmó la aprobación. Verifique el equipo y el voucher antes de reintentar."
                    : "El equipo no devolvió voucher: cobro cancelado o sin respuesta.";

            // 05 = "no honrar": lo rechaza el BANCO EMISOR, no el equipo.
            if (r.CodigoRespuesta == "05")
            {
                return "Cobro DENEGADO por el banco de la tarjeta: " + detalle +
                       "\n\nNo lo reintente con la misma tarjeta: pida otra o cobre de otra forma.";
            }

            return "Cobro no realizado: " + detalle;
        }

        /// <summary>
        /// Arma la trama TLV que se envía al equipo para cobrar <paramref name="monto"/>,
        /// sin enviar nada ni necesitar el pinpad conectado: sirve para inspeccionar
        /// el mensaje real (p. ej. volcarlo a la bitácora o compararlo con Niubiz).
        ///
        /// Ojo con la forma: NO es texto plano ni un prefijo de operación. Es un
        /// documento TLV con tag padre <c>0xFC</c>, tags binarios de 3 bytes
        /// (<c>DF FF 25</c>…) y el monto en centavos BCD de 6 bytes, no en dígitos
        /// ASCII. La operación viaja aparte, en su propio tag y como un byte.
        /// </summary>
        public static string ConstruirTramaPCL(decimal monto) =>
            ArmarTrama(OperacionCompra, monto);

        /// <summary>Redondea a centavos y valida contra los límites del protocolo.</summary>
        private static decimal NormalizarMonto(decimal monto)
        {
            if (monto <= 0)
                throw new ArgumentException("El monto debe ser mayor a cero.", nameof(monto));

            decimal montoRedondeado = decimal.Round(monto, 2, MidpointRounding.AwayFromZero);
            if (montoRedondeado > 9999999999.99m)
                throw new ArgumentOutOfRangeException(nameof(monto), "El monto excede el máximo admitido por el pinpad.");

            return montoRedondeado;
        }

        /// <summary>Centavos tal como los espera el tag del monto (formato D12).</summary>
        private static long ACentavos(decimal monto) =>
            decimal.ToInt64(NormalizarMonto(monto) * 100m);

        /// <summary>
        /// Arma la trama TLV con los mismos tags y valores del legacy.
        /// Monto y cuotas solo viajan en la COMPRA; la referencia, solo en la
        /// ANULACIÓN (identifica qué operación del lote se deshace).
        /// </summary>
        private static string ArmarTrama(long operacion, decimal? monto, string referenciaAnular = null)
        {
            var tlv = new TLVClass(TagPadre);
            XElement raiz = tlv.GetParentElement();
            DateTime ahora = DateTime.Now;

            // transactionId: "1" en las operaciones de dinero (compra y anulación),
            // "4" en las de mantenimiento (eco, inicialización, cierre). Son los
            // valores del legacy — con el número equivocado el equipo descarta el
            // mensaje sin siquiera responder.
            bool operacionDeDinero = operacion == OperacionCompra || operacion == OperacionAnulacion;

            tlv.AddTLVString(ref raiz, TagIdExternalDevice, IdDeEstaMaquina());
            tlv.AddTLVString(ref raiz, TagTransactionId, operacionDeDinero ? "1" : "4");
            tlv.AddTLVInteger(ref raiz, TagFecha, long.Parse(ahora.ToString("yyMMdd")));
            tlv.AddTLVInteger(ref raiz, TagHora, long.Parse(ahora.ToString("HHmmss")));
            tlv.AddTLVByte(ref raiz, TagOperacion, operacion);
            tlv.AddTLVString(ref raiz, TagCashierNumber, "1");
            if (monto.HasValue)
            {
                tlv.AddTLVInteger(ref raiz, TagMonto, ACentavos(monto.Value), "D12");
                tlv.AddTLVInteger(ref raiz, TagCuotas, 0);
            }
            if (!string.IsNullOrEmpty(referenciaAnular))
                tlv.AddTLVString(ref raiz, TagReferenciaAnular, referenciaAnular);

            return tlv.SerializeTLVAscii();
        }

        /// <summary>Bitácora simple junto al ejecutable, para diagnosticar el enlace.</summary>
        private static void Log(string linea)
        {
            try
            {
                System.IO.File.AppendAllText(
                    RutaLog,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + linea + Environment.NewLine);
            }
            catch
            {
                // La bitácora jamás debe tumbar un cobro.
            }
        }

        /// <summary>
        /// Identificador de esta caja ante el pinpad: la MAC de la primera interfaz,
        /// recortada a 10 caracteres (convención del legacy).
        /// </summary>
        private static string IdDeEstaMaquina()
        {
            string mac = NetworkInterface.GetAllNetworkInterfaces()[0]
                .GetPhysicalAddress().ToString();
            return mac.Length > 10 ? mac.Substring(0, 10) : mac;
        }

        /// <summary>
        /// Extrae autorización/lote/referencia/terminal/captura del TEXTO del voucher.
        /// El formato es el del ticket de Niubiz:
        ///   "TER: 00012345  LOTE: 012  REF: 001234"
        ///   "AP: 123456     FECHA: ..."
        ///   "CAPTURA: CTLS-..."
        /// Pura y estática a propósito: es la parte frágil (si Niubiz cambia el
        /// ticket, se ajusta acá y se cubre con un test de texto).
        /// </summary>
        public static void ParsearVoucher(string voucher, CobroTarjetaResultado destino)
        {
            if (string.IsNullOrEmpty(voucher)) return;

            foreach (string linea in voucher.Split('\n'))
            {
                if (linea.Contains("LOTE"))
                {
                    destino.TerminalPos = EntreMarcas(linea, "TER:", "LOTE");
                    destino.Lote = EntreMarcas(linea, "LOTE:", "REF:");
                    destino.Referencia = DespuesDe(linea, "REF:");
                }

                if (linea.Contains("FECHA"))
                    destino.Autorizacion = EntreMarcas(linea, "AP:", "FECHA");

                if (linea.Contains("CAPTURA:"))
                    destino.Captura = EntreMarcas(linea, "CAPTURA:", "-");
            }
        }

        /// <summary>
        /// Deduce si el cobro se hizo con billetera mirando lo que reportó el
        /// equipo (marca y tipo de cuenta). Pura y estática a propósito: es la
        /// parte que habrá que ajustar cuando Niubiz confirme el literal que
        /// manda en una venta Yape/Plin.
        /// </summary>
        public static bool EsBilletera(params string[] valores)
        {
            if (valores == null) return false;

            foreach (string valor in valores)
            {
                if (string.IsNullOrWhiteSpace(valor)) continue;

                foreach (string nombre in NombresDeBilletera)
                {
                    if (valor.IndexOf(nombre, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Texto entre el fin de `desde` y el inicio de `hasta` (sin espacios).</summary>
        private static string EntreMarcas(string linea, string desde, string hasta)
        {
            int inicio = linea.IndexOf(desde, StringComparison.Ordinal);
            if (inicio < 0) return string.Empty;
            inicio += desde.Length;
            int fin = linea.IndexOf(hasta, inicio, StringComparison.Ordinal);
            if (fin < 0) fin = linea.Length;
            return linea.Substring(inicio, fin - inicio).Trim().Replace(" ", "");
        }

        private static string DespuesDe(string linea, string marca)
        {
            int inicio = linea.IndexOf(marca, StringComparison.Ordinal);
            if (inicio < 0) return string.Empty;
            return linea.Substring(inicio + marca.Length).Trim().Replace(" ", "");
        }
    }
}
