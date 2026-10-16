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
        /// <summary>Datos del comprobante a imprimir. Obligatorio.</summary>
        public ComprobanteRequest venta { get; set; }

        /// <summary>Datos del emisor (RUC y razón social son obligatorios).</summary>
        public EmisorRequest emisor { get; set; }

        /// <summary>Cajero, turno y equipo que firman el pie del ticket.</summary>
        public ContextoRequest contexto { get; set; }

        /// <summary>
        /// Marca el ticket como copia. Va en falso por omisión: la primera
        /// impresión es el original y sólo un reintento deliberado del operador
        /// debe pedir la copia.
        /// </summary>
        public bool copia { get; set; }
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
