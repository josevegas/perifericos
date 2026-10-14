using System;
using System.Text;

namespace PuntoVenta.Infrastructure.Impresion
{
    /// <summary>
    /// Los comandos ESC/POS que usa el ticket, y nada más.
    ///
    /// Están acá con nombre y no repartidos como <c>"\x1B\x61\x01"</c> por el
    /// código: un byte suelto en medio de una cadena no se puede leer ni buscar.
    ///
    /// Medido en papel sobre la EPSON TM-T20III de la caja: <see cref="Columnas"/>
    /// = 64 en fuente B, papel de 80 mm.
    /// </summary>
    public static class EscPos
    {
        private const char ESC = (char)27;
        private const char GS = (char)29;

        /// <summary>
        /// Caracteres por línea con <see cref="FuenteB"/> sobre papel de 80 mm.
        ///
        /// La impresora tiene 576 puntos imprimibles. La fuente A gasta 12 por
        /// carácter (576/12 = 48, MEDIDO en papel con una regla marcada cada 5)
        /// y la B gasta 9, de donde salen estos 64.
        ///
        /// Ese 48 medido es lo que da confianza en el 64: confirma el ancho de
        /// 576 puntos del que se deduce. Antes acá decía 42, un número que
        /// nunca se midió y que la prueba que lo "confirmaba" no podía refutar
        /// —imprimía una regla de exactamente 42, que obviamente entraba—.
        ///
        /// Con papel de 58 mm serían menos; habría que volver a medirlo.
        /// </summary>
        public const int Columnas = 64;

        /// <summary>
        /// Página de códigos latina multilingüe (858), <c>ESC t 19</c> en Epson.
        ///
        /// LO IMPORTANTE NO ES CUÁL, ES QUE SE DECLARE. Los bytes altos —los de
        /// <c>ñ á é í ó ú °</c>— significan un caracter distinto en cada página,
        /// así que la de la impresora y la de <see cref="Codificacion"/> tienen
        /// que ser la misma o el apellido del cliente sale en símbolos raros. Sin
        /// mandar <c>ESC t</c> se depende de con cuál venga configurada la caja,
        /// y el driver de Epson permite cambiarla.
        ///
        /// Se elige la 858 y no la 437 de fábrica porque cubre todo el latino
        /// (es la 850 más el euro); la 437 alcanza para el español justo, pero
        /// se queda corta apenas aparece otro caracter.
        /// </summary>
        public const int PaginaCodigos = 19;

        /// <summary>Cómo se codifican los bytes: tiene que ser la MISMA que
        /// <see cref="PaginaCodigos"/> o la impresora dibuja otros símbolos.</summary>
        public static Encoding Codificacion
        {
            get { return Encoding.GetEncoding(858); }
        }

        /// <summary>Espaciado entre caracteres a 0 (<c>ESC SP 0</c>).
        ///
        /// En ESTA impresora es redundante —su default ya es 0, comprobado— y
        /// además <c>ESC @</c> lo restablece. Se manda igual porque el ancho de
        /// <see cref="Columnas"/> depende de él: dejarlo explícito hace que el
        /// número siga siendo cierto aunque otra caja venga con el driver
        /// configurado distinto. Cuesta tres bytes por ticket.
        /// </summary>
        private static readonly string EspaciadoCero = ESC + " " + (char)0;

        /// <summary>La fuente chica (<c>ESC M 1</c>): 9 puntos de ancho contra
        /// los 12 de la fuente A.
        ///
        /// ESC/POS solo tiene estas dos y no hay tamaños intermedios. Se eligió
        /// la B porque en el papel de 80 mm la A dejaba la tabla del detalle sin
        /// espacio: con 64 columnas cada dato tiene su columna sin recortes.
        /// </summary>
        private static readonly string FuenteB = ESC + "M" + (char)1;

        /// <summary>Deja la impresora en un estado conocido: juego latino,
        /// espaciado cero y la fuente chica.
        ///
        /// Va al principio de CADA ticket: el anterior pudo dejarla en negrita,
        /// centrada, con otro espaciado o en la otra fuente, y un ticket no
        /// puede depender de cómo terminó el de antes.
        /// </summary>
        public static readonly string Inicio =
            ESC + "@" + ESC + "t" + (char)PaginaCodigos + EspaciadoCero + FuenteB;

        public static readonly string Izquierda = ESC + "a" + (char)0;
        public static readonly string Centro = ESC + "a" + (char)1;
        public static readonly string Derecha = ESC + "a" + (char)2;

        public static readonly string NegritaOn = ESC + "E" + (char)1;
        public static readonly string NegritaOff = ESC + "E" + (char)0;

        /// <summary>Doble ancho y doble alto: para el nombre del grifo.</summary>
        public static readonly string Grande = GS + "!" + (char)17;
        public static readonly string Normal = GS + "!" + (char)0;

        /// <summary>
        /// Corte parcial dejando avanzar el papel.
        ///
        /// Parcial y no total: queda un puntito de papel unido que impide que el
        /// ticket se caiga al piso antes de que el cajero lo tome.
        /// </summary>
        public static readonly string Cortar = GS + "V" + (char)66 + (char)3;

        /// <summary>Abre la gaveta del cajón (pin 2). Sin gaveta no hace nada.</summary>
        public static readonly string AbrirGaveta =
            ESC + "p" + (char)0 + (char)25 + (char)250;

        /// <summary>Una línea de guiones del ancho del papel.</summary>
        public static string Separador()
        {
            return new string('-', Columnas);
        }

        /// <summary>
        /// Texto a la izquierda, importe a la derecha, en la misma línea.
        ///
        /// Es el 90% de un ticket ("TOTAL" ... "S/ 5.00") y hacerlo con espacios
        /// contados a ojo en cada llamada es de donde salen los tickets
        /// desalineados.
        /// </summary>
        public static string Fila(string izquierda, string derecha)
        {
            izquierda = izquierda ?? string.Empty;
            derecha = derecha ?? string.Empty;

            int relleno = Columnas - izquierda.Length - derecha.Length;
            if (relleno < 1)
            {
                // No entra: se recorta la etiqueta, NUNCA el importe. Un monto a
                // medias es un dato falso; una descripción cortada se entiende.
                int disponible = Columnas - derecha.Length - 1;
                if (disponible < 1) return derecha;
                izquierda = izquierda.Substring(0, Math.Min(izquierda.Length, disponible));
                relleno = Columnas - izquierda.Length - derecha.Length;
            }
            return izquierda + new string(' ', relleno) + derecha;
        }

        /// <summary>Centra sin usar el comando, para mezclar con texto alineado.</summary>
        public static string Centrado(string texto)
        {
            texto = texto ?? string.Empty;
            if (texto.Length >= Columnas) return texto;
            return new string(' ', (Columnas - texto.Length) / 2) + texto;
        }

        /// <summary>
        /// Parte un texto largo en líneas que entran en el papel.
        ///
        /// Hace falta para la dirección del grifo y la razón social del cliente:
        /// sin esto la impresora corta la palabra donde se le acabe el ancho.
        /// </summary>
        public static string[] Envolver(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return new string[0];

            var lineas = new System.Collections.Generic.List<string>();
            var linea = new StringBuilder();

            foreach (string palabra in texto.Trim().Split(' '))
            {
                if (palabra.Length == 0) continue;

                if (linea.Length == 0) linea.Append(palabra);
                else if (linea.Length + 1 + palabra.Length <= Columnas)
                {
                    linea.Append(' ').Append(palabra);
                }
                else
                {
                    lineas.Add(linea.ToString());
                    linea.Clear().Append(palabra);
                }
            }
            if (linea.Length > 0) lineas.Add(linea.ToString());
            return lineas.ToArray();
        }
    }
}
