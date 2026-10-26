using System;

namespace PuntoVenta.Models.Impresion
{
    /// <summary>
    /// Constancia del comprobante electrónico que emitió Bizlinks.
    ///
    /// ESTOS DATOS NO SE PUEDEN INVENTAR. El QR, el hash y el texto de
    /// autorización son la prueba de que SUNAT aceptó el comprobante; si la
    /// venta no se emitió electrónicamente, esto va en <c>null</c> y el ticket
    /// sale sin ese bloque. Imprimirlo con datos de relleno sería falsificar
    /// un documento tributario.
    /// </summary>
    public sealed class ComprobanteElectronicoImpresion
    {
        /// <summary>
        /// Cadena (código hash) que identifica el comprobante ante SUNAT. Va
        /// impresa sobre el QR, para poder dictarla por teléfono si el lector
        /// no funciona.
        /// </summary>
        public string Hash { get; set; }

        /// <summary>
        /// Lo que codifica el QR. Si no viene se usa el <see cref="Hash"/>, que
        /// es lo que trae el comprobante real.
        /// </summary>
        public string ContenidoQr { get; set; }

        /// <summary>
        /// Resolución de SUNAT que autoriza la representación impresa. Si no
        /// viene se usa la que sale hoy en los comprobantes de la empresa.
        /// </summary>
        public string Resolucion { get; set; }

        /// <summary>
        /// Dónde se valida el comprobante. Si no viene se usa el portal de
        /// Bizlinks que figura en el comprobante real.
        /// </summary>
        public string UrlValidacion { get; set; }

        /// <summary>
        /// Hay algo que imprimir: el hash o el contenido del QR. Un objeto
        /// vacío no alcanza —un QR sin datos no valida nada— y se omite.
        /// </summary>
        public bool PuedeImprimir()
        {
            return !string.IsNullOrWhiteSpace(Hash) ||
                   !string.IsNullOrWhiteSpace(ContenidoQr);
        }

        public string ContenidoDelQr()
        {
            return string.IsNullOrWhiteSpace(ContenidoQr)
                ? (Hash ?? string.Empty).Trim()
                : ContenidoQr.Trim();
        }
    }
}
