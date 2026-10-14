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
        public decimal Total { get; set; }
        public decimal Vuelto { get; set; }
        public string CondicionPago { get; set; }
        public LineaComprobanteImpresion Linea { get; set; }
        public List<PagoComprobanteImpresion> Pagos { get; set; }
    }

    public sealed class LineaComprobanteImpresion
    {
        public string Descripcion { get; set; }
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
        public string Telefono { get; set; }

        public bool PuedeEmitir()
        {
            return !string.IsNullOrWhiteSpace(Ruc) &&
                   !string.IsNullOrWhiteSpace(RazonSocial);
        }

        public string Ubicacion()
        {
            var partes = new List<string>();
            if (!string.IsNullOrWhiteSpace(Distrito)) partes.Add(Distrito.Trim());
            if (!string.IsNullOrWhiteSpace(Provincia)) partes.Add(Provincia.Trim());
            if (!string.IsNullOrWhiteSpace(Departamento)) partes.Add(Departamento.Trim());
            return string.Join(" - ", partes);
        }
    }
}
