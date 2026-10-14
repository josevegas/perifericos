using System;
using System.Text;

namespace PuntoVenta.Infrastructure.Impresion
{
    /// <summary>
    /// El monto en letras del ticket ("SON: VEINTE SOLES").
    ///
    /// Escrito de cero y no portado del legacy: <c>DoHash.cs.toText()</c> tiene
    /// la conversión mezclada adentro del método que dibuja el ticket, con
    /// <c>if/else if</c> encadenados que no se pueden probar sueltos. Acá es una
    /// función pura: entra un importe, sale el texto.
    ///
    /// Cubre hasta 999,999,999 — muy por encima de lo que factura un despacho
    /// de combustible.
    /// </summary>
    public static class NumeroEnLetras
    {
        private static readonly string[] Unidades =
        {
            "", "UNO", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE",
            "OCHO", "NUEVE", "DIEZ", "ONCE", "DOCE", "TRECE", "CATORCE",
            "QUINCE", "DIECISEIS", "DIECISIETE", "DIECIOCHO", "DIECINUEVE",
        };

        private static readonly string[] Decenas =
        {
            "", "", "VEINTE", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA",
            "SETENTA", "OCHENTA", "NOVENTA",
        };

        private static readonly string[] Centenas =
        {
            "", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS",
            "QUINIENTOS", "SEISCIENTOS", "SETECIENTOS", "OCHOCIENTOS",
            "NOVECIENTOS",
        };

        /// <summary>
        /// "SON: VEINTE SOLES" o "SON: DIEZ CON 50/100 SOLES".
        ///
        /// No hace la apocope de "UNO" a "UN" antes de "SOL/SOLES" (diria
        /// "VEINTIUNO SOLES" y no "VEINTIUN SOLES"): el legacy tampoco la hace,
        /// y esto ya opera en produccion sin que nadie lo reclame.
        /// </summary>
        public static string Son(decimal monto)
        {
            if (monto < 0) monto = 0;

            long entero = (long)Math.Truncate(monto);
            int centavos = (int)Math.Round((monto - entero) * 100m, MidpointRounding.AwayFromZero);

            string texto = DeEntero(entero);
            string moneda = entero == 1 && centavos == 0 ? "SOL" : "SOLES";

            return centavos > 0
                ? $"SON: {texto} CON {centavos:D2}/100 {moneda}"
                : $"SON: {texto} {moneda}";
        }

        private static string DeEntero(long valor)
        {
            if (valor == 0) return "CERO";

            var partes = new StringBuilder();
            if (valor >= 1_000_000)
            {
                long millones = valor / 1_000_000;
                partes.Append(millones == 1 ? "UN MILLON" : DeCientos(millones) + " MILLONES");
                valor %= 1_000_000;
                if (valor > 0) partes.Append(' ');
            }
            if (valor >= 1000)
            {
                long miles = valor / 1000;
                partes.Append(miles == 1 ? "MIL" : DeCientos(miles) + " MIL");
                valor %= 1000;
                if (valor > 0) partes.Append(' ');
            }
            if (valor > 0) partes.Append(DeCientos(valor));

            return partes.ToString();
        }

        /// <summary>Un numero de 1 a 999. "CIEN" es un caso propio: "CIENTO" solo
        /// vale seguido de otra cosa ("CIENTO UNO"), nunca solo.</summary>
        private static string DeCientos(long valor)
        {
            if (valor == 100) return "CIEN";

            var partes = new StringBuilder();
            long centena = valor / 100;
            long resto = valor % 100;

            if (centena > 0) partes.Append(Centenas[centena]);
            if (centena > 0 && resto > 0) partes.Append(' ');
            if (resto > 0) partes.Append(DeDecenas(resto));

            return partes.ToString();
        }

        /// <summary>Un numero de 1 a 99, con las contracciones del español:
        /// "VEINTIUNO" y no "VEINTE Y UNO"; a partir de 30 si lleva "Y"
        /// ("TREINTA Y UNO").</summary>
        private static string DeDecenas(long valor)
        {
            if (valor < 20) return Unidades[valor];
            if (valor < 30)
                return valor == 20 ? "VEINTE" : "VEINTI" + Unidades[valor - 20];

            long decena = valor / 10;
            long unidad = valor % 10;
            return unidad == 0 ? Decenas[decena] : Decenas[decena] + " Y " + Unidades[unidad];
        }
    }
}
