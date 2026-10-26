using PuntoVenta.Models.Impresion;
using PuntoVenta.Services;
using System.Collections.Generic;

namespace PuntoVenta.Perifericos.Host
{
    /// <summary>
    /// Cuerpo de <c>POST /api/v1/pinpad/cobrar</c>.
    /// </summary>
    public sealed class CobroPinpadRequest
    {
        /// <summary>Importe a cobrar, en soles.</summary>
        public decimal monto { get; set; }
    }

    /// <summary>
    /// Cuerpo de <c>POST /api/v1/pinpad/anular</c>.
    /// </summary>
    public sealed class AnulacionPinpadRequest
    {
        /// <summary>
        /// La referencia del cobro que se deshace: es el «REF» que salió
        /// impreso en el voucher, no el código de autorización. El equipo
        /// identifica por referencia la operación que tiene que anular, así que
        /// mandarle la autorización haría que no encuentre nada que anular.
        /// </summary>
        public string referencia { get; set; }
    }

    /// <summary>
    /// Cuerpo de <c>POST /api/v1/impresion/comprobante</c>.
    ///
    /// Es un reflejo del modelo de la librería y no el modelo mismo: el contrato
    /// HTTP va en snake_case (igual que las respuestas) y el modelo va en
    /// PascalCase porque es código .NET. Traducir acá deja los dos nombres
    /// estables por separado: renombrar un campo del backend no obliga a tocar
    /// la librería ni al revés.
    /// </summary>
    public sealed class ImpresionComprobanteRequest
    {
        /// <summary>
        /// Discriminante del papel que se pide. Vacío o ausente significa
        /// «ticket de venta», que es lo único que existía antes de que este
        /// campo se agregara: un cliente viejo tiene que seguir funcionando sin
        /// mandarlo.
        /// </summary>
        public string documento { get; set; }

        /// <summary>Datos del comprobante a imprimir. Lo usan los tickets.</summary>
        public ComprobanteRequest venta { get; set; }

        /// <summary>
        /// Datos de la constancia de anulación. Sólo viaja cuando
        /// <see cref="documento"/> vale <see cref="DocumentoConstanciaAnulacion"/>.
        /// </summary>
        public AnulacionRequest anulacion { get; set; }

        /// <summary>Datos del emisor (RUC y razón social son obligatorios).</summary>
        public EmisorRequest emisor { get; set; }

        /// <summary>Cajero, turno y equipo que firman el pie del ticket.</summary>
        public ContextoRequest contexto { get; set; }

        /// <summary>
        /// Constancia del comprobante electrónico que devolvió Bizlinks. Va acá,
        /// al mismo nivel que <c>venta</c>, que es donde la manda el backend.
        /// Es opcional: sin ella el ticket sale sin QR, como una venta no
        /// electrónica.
        /// </summary>
        public ElectronicoRequest electronico { get; set; }

        /// <summary>
        /// Marca el ticket como copia. Va en falso por omisión: la primera
        /// impresión es el original y sólo un reintento deliberado del operador
        /// debe pedir la copia.
        /// </summary>
        public bool copia { get; set; }

        /// <summary>
        /// Arma el comprobante con su constancia electrónica.
        ///
        /// La constancia se toma de la raíz —donde la manda el backend— y, si no
        /// viene ahí, de la que traiga la venta: el bloque del QR depende de que
        /// este dato no se pierda en el camino.
        /// </summary>
        public ComprobanteImpresion AModelo()
        {
            if (EsConstanciaDeAnulacion()) return null;

            ComprobanteImpresion modelo = venta != null ? venta.AModelo() : null;
            if (modelo != null && electronico != null)
                modelo.Electronico = electronico.AModelo() ?? modelo.Electronico;
            return modelo;
        }

        /// <summary>Valor de <see cref="documento"/> para la constancia de anulación.</summary>
        public const string DocumentoConstanciaAnulacion = "CONSTANCIA_ANULACION";

        /// <summary>
        /// Si este sobre pide la constancia de anulación.
        ///
        /// Se acepta tanto el discriminante explícito como la sola presencia de
        /// <see cref="anulacion"/>: un ticket de venta nunca trae ese bloque, así
        /// que reconocerlo por el bloque —igual que el ticket se reconoce por
        /// <see cref="venta"/>— evita que un backend que todavía no manda el
        /// campo termine imprimiendo una constancia como si fuera una venta.
        /// </summary>
        public bool EsConstanciaDeAnulacion()
        {
            string marcado = (documento ?? string.Empty).Trim();
            if (marcado.Length > 0)
            {
                return string.Equals(
                    marcado, DocumentoConstanciaAnulacion,
                    System.StringComparison.OrdinalIgnoreCase);
            }

            return anulacion != null;
        }

        /// <summary>
        /// Arma la constancia, o <c>null</c> si el sobre es de un ticket.
        ///
        /// Devuelve <c>null</c> —y no lanza— también cuando el bloque
        /// <c>anulacion</c> viene incompleto: el host decide si eso es un 400 por
        /// contrato o un error de impresión, y acá no se puede distinguir.
        /// </summary>
        public ConstanciaAnulacionImpresion AConstancia()
        {
            if (!EsConstanciaDeAnulacion() || anulacion == null) return null;
            return anulacion.AModelo();
        }
    }

    /// <summary>
    /// Datos de la constancia de anulación.
    ///
    /// Es el papel que se entrega al cliente cuando se le devuelve el importe.
    /// No es un comprobante tributario: lo que dice es qué documento quedó sin
    /// efecto —número, fecha y hash del original—, por qué se anuló y cuánto se
    /// devolvió.
    /// </summary>
    public sealed class AnulacionRequest
    {
        /// <summary>Número completo del comprobante anulado, ej. <c>B001-00000001</c>.</summary>
        public string comprobante { get; set; }

        /// <summary>«BOLETA DE VENTA», «FACTURA»; es texto, no el código SUNAT.</summary>
        public string tipo_comprobante { get; set; }

        /// <summary>Fecha de emisión del comprobante anulado, ISO <c>YYYY-MM-DD</c>.</summary>
        public string fecha_emision { get; set; }

        /// <summary>Hora de emisión del comprobante anulado, <c>HH:MM:SS</c>.</summary>
        public string hora_emision { get; set; }

        /// <summary>DNI o RUC del cliente que recibió el comprobante.</summary>
        public string cliente_documento { get; set; }

        /// <summary>Lo que se cobró en su momento.</summary>
        public decimal importe_total { get; set; }

        /// <summary>Fecha en que se anuló, ISO <c>YYYY-MM-DD</c>.</summary>
        public string fecha_anulacion { get; set; }

        /// <summary>Hora en que se anuló, <c>HH:MM:SS</c>.</summary>
        public string hora_anulacion { get; set; }

        /// <summary>Motivo declarado por el cajero; se imprime completo.</summary>
        public string motivo { get; set; }

        /// <summary>Importe que efectivamente se le devuelve al cliente.</summary>
        public decimal monto_devuelto { get; set; }

        /// <summary>«EFECTIVO», «TARJETA», «BILLETERA».</summary>
        public string medio_devuelto { get; set; }

        /// <summary>
        /// Si el comprobante quedó pendiente de baja ante SUNAT. Nulo cuando la
        /// venta no se emitió electrónicamente: en ese caso no hay baja que
        /// tramitar y el renglón no se imprime.
        /// </summary>
        public bool? baja_pendiente { get; set; }

        /// <summary>Estado del comprobante ante SUNAT, tal como lo reportó el PSE.</summary>
        public string estado_comprobante { get; set; }

        /// <summary>
        /// Hash del comprobante ANULADO, no de esta constancia: sirve para
        /// cotejar el papel contra el original que tiene el cliente.
        /// </summary>
        public string hash { get; set; }

        public ConstanciaAnulacionImpresion AModelo()
        {
            return new ConstanciaAnulacionImpresion
            {
                Comprobante = comprobante,
                TipoComprobante = tipo_comprobante,
                FechaEmision = fecha_emision,
                HoraEmision = hora_emision,
                ClienteDocumento = cliente_documento,
                ImporteTotal = importe_total,
                FechaAnulacion = fecha_anulacion,
                HoraAnulacion = hora_anulacion,
                Motivo = motivo,
                MontoDevuelto = monto_devuelto,
                MedioDevuelto = medio_devuelto,
                BajaPendiente = baja_pendiente,
                EstadoComprobante = estado_comprobante,
                Hash = hash,
            };
        }
    }

    /// <summary>Comprobante a imprimir.</summary>
    public sealed class ComprobanteRequest
    {
        public string tipo_documento { get; set; }
        public string serie_numero { get; set; }
        public string fecha_emision { get; set; }
        public string hora_emision { get; set; }
        public string cliente_documento { get; set; }
        public string cliente_razon { get; set; }
        public decimal subtotal { get; set; }
        public decimal igv { get; set; }
        public decimal igv_porcentaje { get; set; }
        public decimal total { get; set; }
        public decimal vuelto { get; set; }
        public string condicion_pago { get; set; }
        public string medios_pago { get; set; }
        public List<LineaRequest> lineas { get; set; }
        /// <summary>Campo legado, aceptado si no se envía <c>lineas</c>.</summary>
        public LineaRequest linea { get; set; }
        public List<PagoRequest> pagos { get; set; }

        /// <summary>
        /// Constancia del comprobante electrónico que devolvió Bizlinks dentro
        /// de la venta. Se sigue aceptando por compatibilidad, pero el backend
        /// la manda en la raíz de la solicitud.
        /// </summary>
        public ElectronicoRequest electronico { get; set; }

        /// <summary>Traduce al modelo de impresión. Devuelve <c>null</c> sin detalle.</summary>
        public ComprobanteImpresion AModelo()
        {
            var detalle = new List<LineaComprobanteImpresion>();
            if (lineas != null)
            {
                foreach (LineaRequest item in lineas)
                {
                    if (item != null && !item.EsVacia())
                    {
                        detalle.Add(item.AModelo());
                    }
                }
            }

            if (detalle.Count == 0 && linea != null && !linea.EsVacia())
                detalle.Add(linea.AModelo());
            if (detalle.Count == 0) return null;

            var modelo = new ComprobanteImpresion
            {
                TipoDocumento = tipo_documento,
                SerieNumero = serie_numero,
                FechaEmision = fecha_emision,
                HoraEmision = hora_emision,
                ClienteDocumento = cliente_documento,
                ClienteRazon = cliente_razon,
                Subtotal = subtotal,
                Igv = igv,
                IgvPorcentaje = igv_porcentaje,
                Total = total,
                Vuelto = vuelto,
                CondicionPago = condicion_pago,
                MediosPago = medios_pago,
                Lineas = detalle,
                Linea = detalle[0],
                Pagos = new List<PagoComprobanteImpresion>(),
                Electronico = electronico != null ? electronico.AModelo() : null,
            };

            if (pagos != null)
            {
                foreach (PagoRequest pago in pagos)
                {
                    if (pago != null)
                    {
                        modelo.Pagos.Add(pago.AModelo());
                    }
                }
            }

            return modelo;
        }
    }

    /// <summary>Detalle de la venta; los datos de despacho son opcionales.</summary>
    public sealed class LineaRequest
    {
        public string descripcion { get; set; }
        public decimal? cantidad { get; set; }
        public string unidad { get; set; }
        /// <summary>Campo legado para el despacho en galones.</summary>
        public decimal galones { get; set; }
        public decimal precio { get; set; }
        public decimal total { get; set; }
        public int? lado { get; set; }
        public int? manguera { get; set; }
        public string placa { get; set; }

        /// <summary>
        /// Una línea sin descripción no se puede imprimir: es lo único que dice
        /// qué se está cobrando. Se exige aunque vengan galones y montos, para
        /// no sacar un ticket con un cargo sin nombre (y para no reventar más
        /// adentro, al armar el papel).
        /// </summary>
        public bool EsVacia()
        {
            return string.IsNullOrWhiteSpace(descripcion);
        }

        public LineaComprobanteImpresion AModelo()
        {
            return new LineaComprobanteImpresion
            {
                Descripcion = descripcion,
                Cantidad = cantidad ?? galones,
                Unidad = string.IsNullOrWhiteSpace(unidad) ? "GLL" : unidad,
                Galones = galones,
                Precio = precio,
                Total = total,
                Lado = lado,
                Manguera = manguera,
                Placa = placa,
            };
        }
    }

    /// <summary>Forma de pago con su número de operación.</summary>
    public sealed class PagoRequest
    {
        public string descripcion { get; set; }
        public decimal monto { get; set; }
        public string numero_operacion { get; set; }

        public PagoComprobanteImpresion AModelo()
        {
            return new PagoComprobanteImpresion
            {
                Descripcion = descripcion,
                Monto = monto,
                NumeroOperacion = numero_operacion,
            };
        }
    }

    /// <summary>
    /// Constancia del comprobante electrónico, tal como la devolvió Bizlinks.
    ///
    /// Nada de esto lo puede deducir la impresora: son datos de la emisión. Si
    /// el backend no los manda —la venta no se emitió electrónicamente, o
    /// Bizlinks aún no respondió— el ticket sale sin el bloque del QR.
    /// </summary>
    public sealed class ElectronicoRequest
    {
        /// <summary>Hash del comprobante; se imprime sobre el QR.</summary>
        public string hash { get; set; }

        /// <summary>Lo que codifica el QR. Si falta se usa el <c>hash</c>.</summary>
        public string contenido_qr { get; set; }

        /// <summary>Resolución que autoriza la representación impresa.</summary>
        public string resolucion { get; set; }

        /// <summary>URL donde el cliente valida el comprobante.</summary>
        public string url_validacion { get; set; }

        public ComprobanteElectronicoImpresion AModelo()
        {
            if (string.IsNullOrWhiteSpace(hash) &&
                string.IsNullOrWhiteSpace(contenido_qr) &&
                string.IsNullOrWhiteSpace(resolucion) &&
                string.IsNullOrWhiteSpace(url_validacion))
            {
                return null;
            }

            return new ComprobanteElectronicoImpresion
            {
                Hash = hash,
                ContenidoQr = contenido_qr,
                Resolucion = resolucion,
                UrlValidacion = url_validacion,
            };
        }
    }

    /// <summary>Emisor del comprobante, tal como sale en la cabecera.</summary>
    public sealed class EmisorRequest
    {
        public string ruc { get; set; }
        public string razon_social { get; set; }
        public string nombre_comercial { get; set; }
        public string direccion { get; set; }
        public string domicilio_fiscal { get; set; }
        public string distrito { get; set; }
        public string provincia { get; set; }
        public string departamento { get; set; }
        public string domicilio_fiscal_distrito { get; set; }
        public string domicilio_fiscal_provincia { get; set; }
        public string domicilio_fiscal_departamento { get; set; }
        public string telefono { get; set; }

        public EmisorImpresion AModelo()
        {
            return new EmisorImpresion
            {
                Ruc = ruc,
                RazonSocial = razon_social,
                NombreComercial = nombre_comercial,
                Direccion = direccion,
                DomicilioFiscal = domicilio_fiscal,
                Distrito = distrito,
                Provincia = provincia,
                Departamento = departamento,
                DomicilioFiscalDistrito = domicilio_fiscal_distrito,
                DomicilioFiscalProvincia = domicilio_fiscal_provincia,
                DomicilioFiscalDepartamento = domicilio_fiscal_departamento,
                Telefono = telefono,
            };
        }
    }

    /// <summary>Pie del ticket: quién atendió, en qué turno y desde qué máquina.</summary>
    public sealed class ContextoRequest
    {
        public string cajero { get; set; }
        public int? turno_numero { get; set; }
        public string nro_serie_maquina { get; set; }
        public string codigo_terminal { get; set; }

        public ContextoImpresion AModelo()
        {
            return new ContextoImpresion(
                cajero, turno_numero, nro_serie_maquina, codigo_terminal);
        }
    }
}
