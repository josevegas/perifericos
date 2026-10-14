using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace PuntoVenta.Infrastructure.Pinpad
{
    /// <summary>
    /// Crea un TLV y lo exporta en XML o con formato TLV en Ascci y en Hexa
    /// </summary>
    class TLVClass
    {
        /// <summary>
        /// Documento prinicipal usado para almacenar los datos del TLV
        /// </summary>
        XDocument Document;
        /// <summary>
        /// Identificador del nodo principal del TLV
        /// </summary>
        string idParent;

        /// <summary>
        /// Constructor,
        /// inicia el objeto TLV desde cero, con el id del nodo padre
        /// </summary>
        /// <param name="id">Identificador del nodo padre</param>
        public TLVClass(int id)
        {
            // Inicia un nuevo TLV
            Document = new XDocument();

            //Le agrega el nodo padre, se agrega una X inicial porque no permite iniciar el nombre del nodo con un numero
            idParent = "x" + id.ToString("X");
            Document.Add(new XElement(idParent));
        }

        /// <summary>
        /// Constructor,
        /// Recibe un string con el formato de TLV para almacenarlo en la clase y poderlo exportar o modificar
        /// </summary>
        /// <param name="TLVSerialize">String que con tiene el TLV</param>
        public TLVClass(string TLVSerialize)
        {
            string Hexa = "";

            // Inicia un nuevo TLV donde se va a almacenar la informacion recibida
            Document = new XDocument();
            Hexa = ToHex(TLVSerialize).Replace(" ", "");


            UnSerialize(Hexa);
        }

        /// <summary>
        /// Constructor,
        /// Recibe un string con el formato de TLV en Hexa para almacenarlo en la clase y poderlo exportar o modificar
        /// </summary>
        /// <param name="Hexa">String que con tiene el TLV en Hexa</param>
        /// <param name="RemoveSpace">indica si se le debe quitar los espacion saltos de linea retornos de carro y tabuladores al string</param>
        public TLVClass(string Hexa, bool RemoveSpace)
        {

            // Inicia un nuevo TLV donde se va a almacenar la informacion recibida
            Document = new XDocument();

            if (RemoveSpace)
                Hexa = Hexa.Replace(" ", "").Replace("\n", "").Replace("\r", "").Replace("\t", "");
           
            UnSerialize(Hexa);
        }

        private void UnSerialize(string Hexa)
        {
            int totalLength = 0;
            XElement Parent;

            // se toma el nodo padrey , se agrega una X inicial porque no permite iniciar el nombre del nodo con un numero
            idParent = "x" + Hexa.Substring(0, 2);
            Document.Add(new XElement(idParent));
            Hexa = Hexa.Substring(2);

            Parent = Document.Element(idParent);

            // obtiene la longitud del tlv
            GetTLVTagLength(ref Hexa, ref totalLength);

            UnSerialize(Hexa, Parent, totalLength);

            //// recorre todo el TVL para agregar nodo por nodo
            //while (count < totalLength)
            //{
            //    string tag = "";
            //    int lengthTag = 0;
            //    string value = "";
            //    Int64 iTag = 0;

            //    // obtiene el tag y quita los caracteres de la cadena
            //    tag = Hexa.Substring(0, 2);
            //    Hexa = Hexa.Substring(2);
            //    count++;

            //    // identifica si el tag es de uno 2 o 3 bytes y tomas los necesarios
            //    // if(tag == "DF" || tag == "9F")
            //    if (Convert.ToInt32(Convert.ToString(Convert.ToInt32(tag, 16), 2).PadLeft(8, '0').Substring(3), 2) == 31)
            //    {
            //        tag += Hexa.Substring(0, 2);
            //        Hexa = Hexa.Substring(2);
            //        count++;

            //        //if (tag == "DFFF")
            //        if (Convert.ToInt32(Convert.ToString(Convert.ToInt32(tag.Substring(2), 16), 2).PadLeft(8, '0').Substring(0, 1), 2) == 1)
            //        {
            //            tag += Hexa.Substring(0, 2);
            //            Hexa = Hexa.Substring(2);
            //            count++;
            //        }
            //    }

            //    // obtienen la longitid del tag
            //    count += GetTLVTagLength(ref Hexa, ref lengthTag);

            //    // toma el valor del tag deacuerdo a la longitud
            //    value = Hexa.Substring(0, lengthTag * 2);
            //    Hexa = Hexa.Substring(lengthTag * 2);
            //    count += lengthTag;

            //    // Agrega el valor del tag al arbol (XML)
            //    iTag = Int64.Parse(tag, System.Globalization.NumberStyles.HexNumber);

            //    // Si el tag es igual a DFFF34 quiere decir que es un nodo padre y se buscan los hijos para agregarlos
            //    if (tag == "DFFF34")
            //    {
            //        // agrega el tag DFFF34 Vacio
            //        AddTLVValue(ref Parent, iTag, "");
            //        XElement ParentChild = GetTLVElement(Parent, iTag);
            //        UnSerialize(value, ParentChild, lengthTag);
            //    }
            //    else
            //        AddTLVValue(ref Parent, iTag, value);
            //}
          //  return Hexa;
        }

        private void UnSerialize(string Hexa, XElement Parent, int totalLength)
        {
            int count = 0;

            while (count < totalLength)
            {
                string tag = "";
                int lengthTag = 0;
                string value = "";
                Int64 iTag = 0;

                // obtiene el tag y quita los caracteres de la cadena
                tag = Hexa.Substring(0, 2);
                Hexa = Hexa.Substring(2);
                count++;

                // identifica si el tag es de uno 2 o 3 bytes y tomas los necesarios
                // if(tag == "DF" || tag == "9F")
                if (Convert.ToInt32(Convert.ToString(Convert.ToInt32(tag, 16), 2).PadLeft(8, '0').Substring(3), 2) == 31)
                {
                    tag += Hexa.Substring(0, 2);
                    Hexa = Hexa.Substring(2);
                    count++;

                    //if (tag == "DFFF")
                    if (Convert.ToInt32(Convert.ToString(Convert.ToInt32(tag.Substring(2), 16), 2).PadLeft(8, '0').Substring(0, 1), 2) == 1)
                    {
                        tag += Hexa.Substring(0, 2);
                        Hexa = Hexa.Substring(2);
                        count++;
                    }
                }

                // obtienen la longitid del tag
                count += GetTLVTagLength(ref Hexa, ref lengthTag);

                // toma el valor del tag deacuerdo a la longitud
                value = Hexa.Substring(0, lengthTag * 2);
                Hexa = Hexa.Substring(lengthTag * 2);
                count += lengthTag;

                // Agrega el valor del tag al arbol (XML)
                iTag = Int64.Parse(tag, System.Globalization.NumberStyles.HexNumber);

                // Si el tag es igual a DFFF34 quiere decir que es un nodo padre y se buscan los hijos para agregarlos
                if (tag == "DFFF34")
                {
                    // agrega el tag DFFF34 Vacio
                    AddTLVValue(ref Parent, iTag, "");
                    XElement ParentChild = GetTLVElement(Parent, iTag);
                    UnSerialize(value, ParentChild, lengthTag);
                }
                else
                    AddTLVValue(ref Parent, iTag, value);
            }
        }




        /// <summary>
        /// obtiene la longitid de la data de un tag
        /// </summary>
        /// <param name="Hexa">Data del TLV, se retorna sin la longitud</param>
        /// <param name="totalLength">retorna la longitud de la data del tag</param>
        /// <returns>retorna el numero de bytes en el que estaba contenida la longitud</returns>
        private static int GetTLVTagLength(ref string Hexa, ref int totalLength)
        {
            int numBytes = 1;
            int len = 0;

            //Toma el primer Byte
            len = Convert.ToInt32(Hexa.Substring(0, 2), 16);
            Hexa = Hexa.Substring(2);

            // Si el primer byte es mayor a 128 toma los siguientes bytes para conocer la longitud
            // sino anigna la longitid del primer byte
            if (len > 128)
            {
                totalLength = Convert.ToInt32(Hexa.Substring(0, (len - 128) * 2), 16);
                Hexa = Hexa.Substring((len - 128) * 2);
                numBytes += len - 128;
            }
            else
            {
                totalLength = len;
            }

            return numBytes;
        }

        /// <summary>
        /// retorna el nodo padre
        /// </summary>
        /// <returns>Elemento padre del arbol</returns>
        public XElement GetParentElement()
        {
            return Document.Element(idParent);
        }

        /// <summary>
        /// Crea un nuevo nodo con un valor string al arbol y lo guarda como viene
        /// </summary>
        /// <param name="internalTLV">Padre del nodo que se desea crear</param>
        /// <param name="tag">Identificador del nodo</param>
        /// <param name="value">Data del nodo</param>
        public void AddTLVValue(ref XElement internalTLV, Int64 tag, string value)
        {
            internalTLV.Add(new XElement("x" + tag.ToString("X"), AddASpaceToHex(value)));
        }

        /// <summary>
        /// Crea un nuevo nodo con un valor string al arbol y lo guarda como hexa
        /// </summary>
        /// <param name="internalTLV">Padre del nodo que se desea crear</param>
        /// <param name="tag">Identificador del nodo</param>
        /// <param name="value">Data del nodo</param>
        public void AddTLVString(ref XElement internalTLV, Int64 tag, string value)
        {
            internalTLV.Add(new XElement("x" + tag.ToString("X"), ToHex(value)));
        }

        /// <summary>
        /// Crea un nuevo nodo con un valor string al arbol y lo guarda como hexa
        /// </summary>
        /// <param name="internalTLV">Padre del nodo que se desea crear</param>
        /// <param name="tag">Identificador del nodo</param>
        /// <param name="value">Data del nodo</param>
        public void AddTLVHex(ref XElement internalTLV, Int64 tag, string value)
        {
            internalTLV.Add(new XElement("x" + tag.ToString("X"), AddASpaceToHex(value.Trim().Replace(" ", string.Empty))));
        }

        /// <summary>
        /// Crea un nuevo nodo con un valor entero al arbol y lo guarda con el formato indicado (D12 para agregar ceros a la izquierda)
        /// </summary>
        /// <param name="internalTLV">Padre del nodo que se desea crear</param>
        /// <param name="tag">Identificador del nodo</param>
        /// <param name="value">Data del nodo</param>
        /// <param name="format">Formato con el que se desea guardar</param>
        public void AddTLVInteger(ref XElement internalTLV, Int64 tag, Int64 value, string format)
        {
            internalTLV.Add(new XElement("x" + tag.ToString("X"), AddASpaceToHex(value.ToString(format))));
        }

        /// <summary>
        /// Crea un nuevo nodo con un valor entero al arbol y lo agrega como viene
        /// </summary>
        /// <param name="internalTLV">Padre del nodo que se desea crear</param>
        /// <param name="tag">Identificador del nodo</param>
        /// <param name="value">Data del nodo</param>
        public void AddTLVInteger(ref XElement internalTLV, Int64 tag, Int64 value)
        {
            internalTLV.Add(new XElement("x" + tag.ToString("X"), AddASpaceToHex(value.ToString())));
        }

        /// <summary>
        /// Crea un nuevo nodo con un valor entero al arbol y lo guarda como hexa
        /// </summary>
        /// <param name="internalTLV">Padre del nodo que se desea crear</param>
        /// <param name="tag">Identificador del nodo</param>
        /// <param name="value">Data del nodo</param>
        public void AddTLVByte(ref XElement internalTLV, Int64 tag, Int64 value)
        {
            internalTLV.Add(new XElement("x" + tag.ToString("X"), AddASpaceToHex(value.ToString("X"))));
        }

        /// <summary>
        /// Modifica el valor de un nodo de tipo string
        /// </summary>
        /// <param name="internalTLV">Nodo que desea modificar</param>
        /// <param name="value">Data del nodo</param>
        public void SetTLVString(ref XElement internalTLV, string value)
        {
            internalTLV.SetValue(ToHex(value));
        }

        /// <summary>
        /// Modifica el valor de un nodo de tipo string
        /// </summary>
        /// <param name="internalTLV">Nodo que desea modificar</param>
        /// <param name="value">Data del nodo</param>
        public void SetTLVHex(ref XElement internalTLV, string value)
        {
            internalTLV.SetValue(AddASpaceToHex(value.Trim().Replace(" ", string.Empty)));
        }

        /// <summary>
        /// Modifica el valor de un nodo de tipo entero
        /// </summary>
        /// <param name="internalTLV">Nodo que desea modificar</param>
        /// <param name="value">Data del nodo</param>
        public void SetTLVInteger(ref XElement internalTLV, Int64 value)
        {
            internalTLV.SetValue(AddASpaceToHex(value.ToString()));
        }

        /// <summary>
        /// Modifica el valor de un nodo de tipo entero con formato
        /// </summary>
        /// <param name="internalTLV">Nodo que desea modificar</param>
        /// <param name="value">Data del nodo</param>
        /// <param name="format">Formato con el que se desea guardar</param>
        public void SetTLVInteger(ref XElement internalTLV, Int64 value, string format)
        {
            internalTLV.SetValue(AddASpaceToHex(value.ToString(format)));
        }

        /// <summary>
        /// Modifica el valor de un nodo de tipo entero guardado como byte
        /// </summary>
        /// <param name="internalTLV">Nodo que desea modificar</param>
        /// <param name="value">Data del nodo</param>
        public void SetTLVByte(ref XElement internalTLV, Int64 value)
        {
            internalTLV.SetValue(AddASpaceToHex(value.ToString("X")));
        }

        /// <summary>
        /// obtiene un nodo especifico
        /// </summary>
        /// <param name="internalTLV">nodo padre del nodo que se desea buscar</param>
        /// <param name="tag">Identificador del nodo a buscar</param>
        /// <returns>Elemento encontrado, null si no existe</returns>
        public XElement GetTLVElement(XElement internalTLV, Int64 tag)
        {
            return internalTLV.Element("x" + tag.ToString("X"));
        }

        /// <summary>
        /// obtiene un nodo especifico
        /// </summary>
        /// <param name="internalTLV">nodo padre del nodo que se desea buscar</param>
        /// <param name="tag">Identificador del nodo a buscar</param>
        /// <param name="Pos">Posicion del nodo, si es un nodo que esta repetido y se quiere traer el segundo se coloca 1</param>
        /// <returns>Elemento encontrado, null si no existe</returns>
        public XElement GetTLVElement(XElement internalTLV, Int64 tag, int Pos)
        {
            int i = 0;
            IEnumerable<XElement> Elements = from el in internalTLV.Elements("x" + tag.ToString("X")) select el;

            foreach (XElement e in Elements)
            {
                if(i == Pos)
                    return e;
                i++;
            }

            return null;
        }

        /// <summary>
        /// obtiene el valor de un nodo tal como esta guardado
        /// </summary>
        /// <param name="internalTLV">Nodo del que se desea traer la data</param>
        /// <returns>Valor del nodo</returns>
        public string GetTLVValue(XElement internalTLV)
        {
            return internalTLV.Value;
        }

        /// <summary>
        /// obtiene el valor de un nodo en formato ascii
        /// </summary>
        /// <param name="internalTLV">Nodo del que se desea traer la data</param>
        /// <returns>Valor del nodo</returns>
        public string GetTLVValueAscii(XElement internalTLV)
        {
            return ToAscii(GetTLVValue(internalTLV));
        }

        /// <summary>
        /// obtiene el valor de un nodo en formato numerico
        /// </summary>
        /// <param name="internalTLV">Nodo del que se desea traer la data</param>
        /// <returns>Valor del nodo</returns>
        public Int64 GetTLVValueInteger(XElement internalTLV)
        {
            return Int64.Parse(GetTLVValue(internalTLV).Replace(" ", ""));
        }

        /// <summary>
        /// obtiene el valor de un nodo en formato byte
        /// </summary>
        /// <param name="internalTLV">Nodo del que se desea traer la data</param>
        /// <returns>Valor del nodo</returns>
        public Int64 GetTLVValueByte(XElement internalTLV)
        {
            return Int64.Parse(GetTLVValue(internalTLV).Replace(" ", ""), System.Globalization.NumberStyles.HexNumber);
        }


        /// <summary>
        /// obtiene la longitiud de la data de un nodo
        /// </summary>
        /// <param name="internalTLV">Nodo del que se desea traer la data</param>
        /// <returns>longitud</returns>
        public int GetTLVLength(XElement internalTLV)
        {
            string value = internalTLV.Value.Replace(" ", "");
            int length = value.Length / 2;
            return length;
        }

        /// <summary>
        /// trae el identificador de un nodo
        /// </summary>
        /// <param name="internalTLV">Nodo del que se desea traer el id</param>
        /// <returns>id del nodo</returns>
        public Int64 GetTLVTag(XElement internalTLV)
        {
            return Int64.Parse(internalTLV.Name.ToString().Substring(1), System.Globalization.NumberStyles.HexNumber);
        }

        /// <summary>
        /// Elimina un nodo especifico
        /// </summary>
        /// <param name="internalTLV">Nodo que se desea eliminar</param>
        public void DeleteTLVTag(ref XElement internalTLV)
        {
            internalTLV.Remove();
        }

        /// <summary>
        /// limpia todo el arbol
        /// </summary>
        public void ReleaeTLV()
        {            
            Document.Remove();
            Document = null;
        }

        /// <summary>
        /// obtiene el tlv en formato hexa
        /// </summary>
        /// <returns>TLV data</returns>
        public string SerializeTLV()
        {
            string TLV = "";
            SerializeTLV(Document.Element(idParent), ref TLV);
            return TLV;
        }

        /// <summary>
        /// Obtiene el TLV en formato Ascii
        /// </summary>
        /// <returns>TLV Data</returns>
        public string SerializeTLVAscii()
        {            
            return ToAscii(SerializeTLV());
        }

        /// <summary>
        /// obtiene el objeto tlv (XDocument)
        /// </summary>
        /// <returns>TLV completo</returns>
        public XDocument GetXMLTLV()
        {
            return Document;
        }

        /// <summary>
        /// Retorna un string en formato XML con los datos del tlv
        /// </summary>
        /// <returns>TLV Data</returns>
        public string GetXMLTLVString()
        {
            return Document.ToString();
        }

        /// <summary>
        /// obtiene el tlv en formato hexa
        /// </summary>
        /// <param name="internalTLV">Elemento que se va a serializar</param>
        /// <param name="TLV">String donde se retorna el TLV serializado</param>
        private void SerializeTLV(XElement internalTLV, ref string TLV)
        {          
            int i = 0;
            string tag;
            string length;
            string Value;
            string LengthLen;
            int iLength;
            string TLV2 = "";

            // recorre los hijos del nodo y hace una llamada recursiva a esta funsion
            IEnumerable<XElement> Elements = GetTLVChildren(internalTLV);
            foreach (XElement e in Elements)
            {
                SerializeTLV(e, ref TLV2);
                i++;
            }

            // si i != 0 quiere decir que es es nodo padre y agrega la longitid total del TLV y el tag principal
            // sino agrega el tag y la longitud del nodo espeficico
            if(i==0)
            {
                tag = AddASpaceToHex(GetTLVTag(internalTLV).ToString("X"));
                LengthLen = "";
                iLength = GetTLVLength(internalTLV);
                if (iLength >= 128)
                {
                    if (iLength > 255)
                        LengthLen = (130).ToString("X");
                    else
                        LengthLen = (130).ToString("X");
                    LengthLen += " ";
                }
                length = AddASpaceToHex(iLength.ToString("X"));
                Value = GetTLVValue(internalTLV);

                TLV += tag + " " + LengthLen + length + " " + Value + " ";
            }else
            {
                tag = AddASpaceToHex(GetTLVTag(internalTLV).ToString("X"));
                LengthLen = "";
                iLength = TLV2.Replace(" ", "").Length / 2;
                if (iLength >= 128)
                {
                    if (iLength > 255)
                        LengthLen = (130).ToString("X");
                    else
                        LengthLen = (129).ToString("X");
                    LengthLen += " ";
                }
                length = AddASpaceToHex(iLength.ToString("X"));
                Value = TLV2;

                TLV = TLV + tag + " " + LengthLen + length + " " + Value;
            }
        }

        /// <summary>
        /// obtiene la lista de hijos de un nodo
        /// </summary>
        /// <param name="internalTLV"></param>
        /// <returns></returns>
        private IEnumerable<XElement> GetTLVChildren(XElement internalTLV)
        {
            IEnumerable<XElement> Elements = from el in internalTLV.Elements() select el;
            
            return Elements;
        }

        /// <summary>
        /// convirte es string ingresado a Ascii
        /// </summary>
        /// <param name="input">String que se desea convertir</param>
        /// <returns>Dato retornado en ascii</returns>
        public static string TLVToAscii(string input)
        {
            return ToAscii(input);
        }

        /// <summary>
        /// Convierte los datos ingresados a hexa con espacio entre cada byte
        /// </summary>
        /// <param name="input">Dato a convertir</param>
        /// <returns>string resultante</returns>
        public static string ToHex(string input)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in input)
                sb.AppendFormat("{0:X2} ", (int)c);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Agrega espacios a un string cada 2 caracteres para simular un formato de hexa
        /// </summary>
        /// <param name="input">dato a convertir</param>
        /// <returns>estring con los espacios</returns>
        private static string AddASpaceToHex(string input)
        {
            int length = input.Length;
            string ret = "";

            if(length % 2 != 0)
            {
                input = "0" + input;
                length = length + 1;
            }

            for (int i = 0; i < length; i+=2)
            {
                ret += input.Substring(i, 2) + " ";
            }

            if (ret.Length > 0)
                return ret.Substring(0, ret.Length - 1);
            else
                return ret;
        }

        /// <summary>
        /// Convierte el string ingresado de hexa a ascii
        /// </summary>
        /// <param name="Data">Dato que se desea convertir</param>
        /// <returns>Dato resultante en ascii</returns>
        private static string ToAscii(string Data)
        {
            int length;
            int j = 0;

            Data = Data.Replace(" ", "");
            length = Data.Length;

            char[] asciiData = new char[length / 2];

            for (int i = 0; i < length; i+=2)
            {
                string hs = Data.Substring(i, 2);
                asciiData[j] = Convert.ToChar(Convert.ToInt32(hs, 16));
                j++;
            }

            string s = new string(asciiData);

            return s;
        }

        /// <summary>
        /// Convierte un entero a ascii
        /// </summary>
        /// <param name="numero">numero que se desea convertir</param>
        /// <returns>Cadena resultante en ascii</returns>
        private static string intToAscii(Int64 numero)
        {
            string cNumero = numero.ToString();
            bool inpar = false;
            int j = 0;
            int length = 0;

            if (cNumero.Length % 2 == 0)
            {
                length = cNumero.Length / 2;
            }
            else
            {
                inpar = true;
                length = (cNumero.Length + 1) / 2;
            }

            char[] ret = new char[length];

            for (int i = 0; i < length; i++)
            {
                if (inpar)
                {
                    ret[i] = Convert.ToChar(Convert.ToInt32(cNumero.Substring(j, 1), 16));
                    j++;
                    inpar = false;
                }
                else
                {
                    ret[i] = Convert.ToChar(Convert.ToInt32(cNumero.Substring(j, 2), 16));
                    j += 2;
                }
            }

            string s = new string(ret);

            return s;
        }

        /// <summary>
        /// Convierte un byte a ascii
        /// </summary>
        /// <param name="numero"></param>
        /// <returns></returns>
        private static string byteToAscii(Int64 numero)
        {
            string cNumero = numero.ToString();
            bool inpar = false;
            int j = 0;
            int length = 0;

            if (cNumero.Length % 2 == 0)
            {
                length = cNumero.Length / 2;
            }
            else
            {
                inpar = true;
                length = (cNumero.Length + 1) / 2;
            }

            char[] ret = new char[length];

            for (int i = 0; i < length; i++)
            {
                if(inpar)
                {
                    ret[i] = Convert.ToChar(int.Parse(cNumero.Substring(j, 1)));
                    j++;
                    inpar = false;
                } else
                {
                    ret[i] = Convert.ToChar(int.Parse(cNumero.Substring(j, 2)));
                    j += 2;
                }
            }

            string s = new string(ret);

            return s;
        }

        /// <summary>
        /// Elimina un nodo especifico
        /// </summary>
        /// <param name="internalTLV">Nodo que se desea eliminar</param>
        public int GetNumOfChild(XElement internalTLV)
        {
            IEnumerable<XNode> Nodes = internalTLV.Nodes();

            return Nodes.Count();
        }

    }
}
