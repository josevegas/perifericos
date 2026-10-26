using System.Collections.Generic;

namespace PuntoVenta.Models.Impresion
{
    /// <summary>Datos mínimos de una venta necesarios para imprimir el comprobante.</summary>
    public sealed class ComprobanteImpresion
    {
        public string TipoDocumento { get; set; }
        public string SerieNumero { get; set; }
        public string FechaEmision { get; set; }
        public string HoraEmision { get; set; }
        public string ClienteDocumento { get; set; }
        public string ClienteRazon { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Igv { get; set; }
        public decimal IgvPorcentaje { get; set; }
        public decimal Total { get; set; }
        public decimal Vuelto { get; set; }
        public string CondicionPago { get; set; }
        public string MediosPago { get; set; }
        public List<LineaComprobanteImpresion> Lineas { get; set; }
        // Compatibilidad con comprobantes antiguos que solo traen una línea.
        public LineaComprobanteImpresion Linea { get; set; }
        public List<PagoComprobanteImpresion> Pagos { get; set; }

        /// <summary>
        /// Constancia de Bizlinks. <c>null</c> cuando la venta no se emitió
        /// electrónicamente: en ese caso el ticket no lleva QR.
        /// </summary>
        public ComprobanteElectronicoImpresion Electronico { get; set; }
    }

    public sealed class LineaComprobanteImpresion
    {
        public string Descripcion { get; set; }
        public decimal? Cantidad { get; set; }
        public string Unidad { get; set; }
        // Compatibilidad con comprobantes antiguos de despacho en galones.
        public decimal Galones { get; set; }
        public decimal Precio { get; set; }
        public decimal Total { get; set; }
        public int? Lado { get; set; }
        public int? Manguera { get; set; }
        public string Placa { get; set; }
    }

    public sealed class PagoComprobanteImpresion
    {
        public string Descripcion { get; set; }
        public decimal Monto { get; set; }
        public string NumeroOperacion { get; set; }
    }

    /// <summary>Datos del emisor requeridos para identificar el comprobante.</summary>
    public sealed class EmisorImpresion
    {
        public string Ruc { get; set; }
        public string RazonSocial { get; set; }
        public string NombreComercial { get; set; }
        public string Direccion { get; set; }
        public string DomicilioFiscal { get; set; }
        public string Distrito { get; set; }
        public string Provincia { get; set; }
        public string Departamento { get; set; }
        public string DomicilioFiscalDistrito { get; set; }
        public string DomicilioFiscalProvincia { get; set; }
        public string DomicilioFiscalDepartamento { get; set; }
        public string Telefono { get; set; }

        public bool PuedeEmitir()
        {
            return !string.IsNullOrWhiteSpace(Ruc) &&
                   !string.IsNullOrWhiteSpace(RazonSocial);
        }

        /// <summary>Ubigeo de la sede del market, como "Distrito - Provincia - Departamento".</summary>
        public string Ubicacion()
        {
            return Unir(Distrito, Provincia, Departamento);
        }

        /// <summary>
        /// Ubigeo del domicilio fiscal, el que SUNAT tiene ligado al RUC. Cae al
        /// de la sede cuando el emisor no manda uno propio: mientras el ubigeo
        /// sea el mismo, el comprobante lo repite debajo de las dos direcciones,
        /// que es como sale hoy el ticket que ya se emite.
        /// </summary>
        public string UbicacionFiscal()
        {
            string fiscal = Unir(
                DomicilioFiscalDistrito, DomicilioFiscalProvincia, DomicilioFiscalDepartamento);

            return fiscal.Length > 0 ? fiscal : Ubicacion();
        }

        private static string Unir(string distrito, string provincia, string departamento)
        {
            var partes = new List<string>();
            if (!string.IsNullOrWhiteSpace(distrito)) partes.Add(distrito.Trim());
            if (!string.IsNullOrWhiteSpace(provincia)) partes.Add(provincia.Trim());
            if (!string.IsNullOrWhiteSpace(departamento)) partes.Add(departamento.Trim());
            return string.Join(" - ", partes);
        }
    }
}
