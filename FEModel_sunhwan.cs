using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Media.Media3D;
using CsvToBdf.AMData;
using CsvToBdf.Control;

// ver.2409

namespace CsvToBdf.FEData
{
    public class FEModel
    {
        private List<Node> _nodeList;
        private List<Node> _pipeNodeList;
        private List<Node> _struNodeList;
        private List<Node> _equiNodeList;
        private List<Prop> _propList;
        private List<Element> _elemList;
        private List<Element> _struElemList;
        private List<Element> _pipeElemList;
        private List<Rbe> _rbeList;
        private List<Mass> _massList;
        private List<Point3D> _struGridList;
        private List<Point3D> _pipeGridList;
        public List<Mat> MatList { get; set; }
        public List<int> SPCList { get; set; }
        public FEModel()
        {
            Init();
        }
        public void Init()
        {
            _nodeList = new List<Node>();
            _propList = new List<Prop>();
            _elemList = new List<Element>();
            _struGridList = new List<Point3D>();
            _pipeGridList = new List<Point3D>();
            _pipeNodeList = new List<Node>();
            _struNodeList = new List<Node>();
            _equiNodeList = new List<Node>();
            _struElemList = new List<Element>();
            _pipeElemList = new List<Element>();
            _rbeList = new List<Rbe>();
            _massList = new List<Mass>();
            MatList = new List<Mat>();
            SPCList = new List<int>();
        }
        public List<Node> NodeList
        {
            get { return _nodeList; }
            set { _nodeList = value; }
        }
        public List<Element> ElemList
        {
            get { return _elemList; }
            set { _elemList = value; }
        }
        public List<Mass> MassList
        {
            get { return _massList; }
            set { _massList = value; }
        }
        public List<Rbe> RbeList
        {
            get { return _rbeList; }
            set { _rbeList = value; }
        }
        public List<Prop> PropList
        {
            get { return _propList; }
        }

        /// <summary>
        /// Pipe List에서 Node를 추출하여 리스트로 저장
        /// </summary>
        /// <param name="pipeList"></param>
        public void AddPipeGrid(List<AMPipe> pipeList)
        {
            List<AMPipe> valvList = new List<AMPipe>();
            List<Point3D> posList = new List<Point3D>();
            List<Point3D> interList;
            foreach (AMPipe pipe in pipeList)
            {
                // Element
                if (pipe.Type == "TUBI" || pipe.Type == "COUP" || pipe.Type == "REDU" || pipe.Type == "OLET")
                {
                    posList.Add(pipe.APos);
                    posList.Add(pipe.LPos);
                }
                else if (pipe.Type == "ELBO" || pipe.Type == "BEND")
                {
                    interList = new List<Point3D>();
                    posList.Add(pipe.APos);
                    posList.Add(pipe.LPos);
                    if (pipe.InterPos.Count > 0)
                    {
                        posList.AddRange(pipe.InterPos);
                    }
                }
                else if (pipe.Type == "TEE")
                {
                    posList.Add(pipe.Pos);
                    posList.Add(pipe.APos);
                    posList.Add(pipe.LPos);
                    posList.Add(pipe.P3Pos);
                }
                else if (pipe.Type == "FLAN")
                {
                    posList.Add(pipe.Pos);
                    if (pipe.APos != pipe.LPos && pipe.OutDia != 0)
                    {
                        posList.Add(pipe.APos);
                        posList.Add(pipe.LPos);
                    }
                }
                // Ubolt
                else if (pipe.Type == "UBOLT")
                {
                    posList.Add(pipe.Pos);
                }
                // RBE + Mass
                else if (pipe.Type == "VALV" || pipe.Type == "TRAP" || pipe.Type == "FILT" || pipe.Type == "EXP")
                {
                    posList.Add(pipe.Pos);
                    posList.Add(pipe.APos);
                    posList.Add(pipe.LPos);
                    valvList.Add(pipe);
                }
                else if (pipe.Type == "VTWA")
                {
                    posList.Add(pipe.Pos);
                    posList.Add(pipe.APos);
                    posList.Add(pipe.LPos);
                    posList.Add(pipe.P3Pos);
                }
            }
            posList = posList.Distinct().ToList();
            posList = posList.OrderBy(s => s.X).ThenBy(s => s.Y).ThenBy(s => s.Z).ToList();
            _pipeNodeList = new List<Node>();
            int id = _nodeList.Count();
            foreach (Point3D pos in posList)
            {
                if (!_pipeNodeList.Any(s => ModelHandle.GetDistance(s, pos) < 21))
                {
                    id += 1;
                    Node node = new Node(pos);
                    node.nodeID = id;
                    _pipeNodeList.Add(node);
                }
            }
            _nodeList.AddRange(_pipeNodeList);
        }

        /// <summary>
        /// PipeList를 Element(RBE, MASS 포함)로 변환하여 리스트에 저장
        /// </summary>
        /// <param name="pipeList"></param>
        public void AddPipeElem(List<AMPipe> pipeList)
        {
            Element newElem;
            Element baseElem;
            List<Node> interList;
            int id = _elemList.Count();
            Node node1;
            Node node2;
            int a = 0;
            foreach (AMPipe pipe in pipeList)
            {
                node1 = GetNodeFromPipeList(pipe.APos);
                node2 = GetNodeFromPipeList(pipe.LPos);

                if (pipe.Type == "TUBI" || pipe.Type == "ELBO" || pipe.Type == "BEND")
                {
                    if (node1 == node2)
                        continue;
                    interList = new List<Node>();
                    if (pipe.Type == "TUBI")
                    {
                        interList = GetInterNodes(pipe);
                        interList = interList.Where(n => ModelHandle.IsNodeOnLineBetweenTwoNode(n, node1, node2)).ToList();
                    }
                    else
                        interList = pipe.InterPos.Select(s => GetNodeFromPipeList(s)).ToList();
                    interList.Add(node1);
                    interList.Add(node2);
                    interList = interList.Distinct().ToList();
                    if (interList.Count > 2)
                    {
                        interList = interList.OrderBy(x => ModelHandle.GetDistance(node1, x)).ToList();
                        // elem 속성 입력
                        baseElem = new Element();
                        baseElem.PropID = GetPropID(pipe.OutDia, pipe.Thick);
                        baseElem.Cood = pipe.Normal;
                        baseElem.AmRef = pipe.Name;
                        for (int i = 0; i < interList.Count() - 1; i++)
                        {
                            id = _elemList.Count() + 1;
                            newElem = new Element(baseElem);
                            newElem.ElemID = id;
                            newElem.Poss = interList[i];
                            newElem.Pose = interList[i + 1];
                            _elemList.Add(newElem);
                        }
                    }
                    else
                    {
                        id = _elemList.Count() + 1;
                        newElem = new Element();
                        newElem.PropID = GetPropID(pipe.OutDia, pipe.Thick);
                        newElem.Cood = pipe.Normal;
                        newElem.AmRef = pipe.Name;
                        newElem.ElemID = id;
                        newElem.Poss = node1;
                        newElem.Pose = node2;
                        _elemList.Add(newElem);
                    }
                }
                else if (pipe.Type == "COUP" || pipe.Type == "REDU" || pipe.Type == "OLET")
                {
                    if (node1 == node2)
                        continue;
                    id = _elemList.Count() + 1;
                    newElem = new Element();
                    newElem.PropID = GetPropID(pipe.OutDia, pipe.Thick);
                    newElem.Cood = pipe.Normal;
                    newElem.AmRef = pipe.Name;
                    newElem.ElemID = id;
                    newElem.Poss = node1;
                    newElem.Pose = node2;
                    _elemList.Add(newElem);
                }
                else if (pipe.Type == "TEE")
                {
                    if (node1 == node2)
                        continue;
                    if (node1 == GetNodeFromPipeList(pipe.Pos))
                        continue;
                    id = _elemList.Count() + 1;
                    newElem = new Element();
                    newElem.PropID = GetPropID(pipe.OutDia, pipe.Thick);
                    newElem.Cood = pipe.Normal;
                    newElem.AmRef = pipe.Name;
                    newElem.ElemID = id;
                    newElem.Poss = node1;
                    newElem.Pose = GetNodeFromPipeList(pipe.Pos);
                    _elemList.Add(newElem);
                    id = _elemList.Count() + 1;
                    newElem = new Element();
                    newElem.PropID = GetPropID(pipe.OutDia, pipe.Thick);
                    newElem.Cood = pipe.Normal;
                    newElem.AmRef = pipe.Name;
                    newElem.ElemID = id;
                    newElem.Poss = GetNodeFromPipeList(pipe.Pos);
                    newElem.Pose = node2;
                    _elemList.Add(newElem);
                    id = _elemList.Count() + 1;
                    newElem = new Element();
                    newElem.PropID = GetPropID(pipe.OutDia2, pipe.Thick2);
                    newElem.Cood = pipe.Normal;
                    newElem.AmRef = pipe.Name;
                    newElem.ElemID = id;
                    newElem.Poss = GetNodeFromPipeList(pipe.Pos);
                    newElem.Pose = GetNodeFromPipeList(pipe.P3Pos);
                    _elemList.Add(newElem);
                }
                else if (pipe.Type == "FLAN")
                {
                    Mass mass = new Mass();
                    mass.ElemID = _massList.Count() + 1;
                    mass.Pos = GetNodeFromPipeList(pipe.Pos);
                    mass.massVal = pipe.Mass;
                    mass.AmRef = pipe.Name;
                    _massList.Add(mass);
                    //System.Diagnostics.Debugger.Launch();
                    if (node1 != node2 && pipe.OutDia != 0)
                    {
                        id = _elemList.Count() + 1;
                        newElem = new Element();
                        newElem.PropID = GetPropID(pipe.OutDia, pipe.Thick);
                        newElem.Cood = pipe.Normal;
                        newElem.AmRef = pipe.Name;
                        newElem.ElemID = id;
                        newElem.Poss = GetNodeFromPipeList(pipe.APos);
                        newElem.Pose = GetNodeFromPipeList(pipe.LPos);
                        _elemList.Add(newElem);
                    }

                }
                // Ubolt
                else if (pipe.Type == "UBOLT")
                {
                    
                    if (pipe.InterPos == null || pipe.InterPos.Count == 0)
                        continue;
                    Rbe rbe = new Rbe();
                    rbe.ElemID = _rbeList.Count() + 1;
                    rbe.Rest = pipe.Rest;
                    rbe.AmRef = pipe.Name;
                    rbe.AMType = "UBOLT";
                    // ubolt중 box type이 아닌 경우는 모두 stru가 Independent, pipe가 dependent로 한다.(stru 한지점에 여러 ubolt가 연결될수 있음)
                    if (pipe.InterPos.Count == 1)
                    {
                        rbe.Pos = GetNodeFromStruList(pipe.InterPos[0]);
                        rbe.DepNodes.Add(GetNodeFromPipeList(pipe.Pos));
                    }
                    else
                    {
                        rbe.Pos = GetNodeFromPipeList(pipe.Pos);
                        pipe.InterPos.ForEach(s => rbe.DepNodes.Add(GetNodeFromStruList(s)));
                    }
                    _rbeList.Add(rbe);
                }
                // RBE + Mass
                else if (pipe.Type == "VALV" || pipe.Type == "TRAP" || pipe.Type == "FILT" || pipe.Type == "EXP")
                {
                    if (node1 == node2)
                        continue;
                    Mass mass = new Mass();
                    mass.ElemID = _massList.Count() + 1;
                    mass.Pos = GetNodeFromPipeList(pipe.Pos);
                    mass.massVal = pipe.Mass;
                    mass.AmRef = pipe.Name;
                    _massList.Add(mass);
                    Rbe rbe = new Rbe();
                    rbe.ElemID = _rbeList.Count() + 1;
                    rbe.Pos = GetNodeFromList(pipe.Pos);
                    //pipe.InterPos.ForEach(s => rbe.DepNodes.Add(GetNodeFromPipeList(s)));
                    rbe.DepNodes.Add(GetNodeFromPipeList(pipe.APos));
                    rbe.DepNodes.Add(GetNodeFromPipeList(pipe.LPos));
                    rbe.Rest = "123456";
                    rbe.AmRef = pipe.Name;
                    _rbeList.Add(rbe);
                }
                else if (pipe.Type == "VTWA")
                {
                    Mass mass = new Mass();
                    mass.ElemID = _massList.Count() + 1;
                    mass.Pos = GetNodeFromPipeList(pipe.Pos);
                    mass.massVal = pipe.Mass;
                    mass.AmRef = pipe.Name;
                    _massList.Add(mass);
                    Rbe rbe = new Rbe();
                    rbe.ElemID = _rbeList.Count() + 1;
                    rbe.Pos = GetNodeFromPipeList(pipe.Pos);
                    rbe.DepNodes.Add(GetNodeFromPipeList(pipe.APos));
                    rbe.DepNodes.Add(GetNodeFromPipeList(pipe.LPos));
                    rbe.DepNodes.Add(GetNodeFromPipeList(pipe.P3Pos));
                    rbe.Rest = "123456";
                    rbe.AmRef = pipe.Name;
                    _rbeList.Add(rbe);
                }
            }
            // renumbering
            //id = _elemList.Count();
            //_rbeList.ForEach(s => s.ElemID += id);
            //id = _elemList.Count() + _rbeList.Count();
            //_massList.ForEach(s => s.ElemID += id);
        }

        /// <summary>
        /// RBE, MASS의 번호를 Element 번호에 맞게 Renumbering
        /// </summary>
        public void Renumbering()
        {
            int rbeStartId = _elemList.Count();
            int massStartId = _elemList.Count() + _rbeList.Count();
            for (int i = 0; i < _elemList.Count; i++)
                _elemList[i].ElemID = i + 1;
            _rbeList.ForEach(s => s.ElemID += rbeStartId);
            _massList.ForEach(s => s.ElemID += massStartId);
        }

        /// <summary>
        /// 연속으로 생성되어 dependent를 공유하는 Rbe를 수정
        /// </summary>
        public void CombineRbes()
        {
            List<Node> depNodes1 = new List<Node>();
            List<Node> depNodes2 = new List<Node>();
            List<Node> duplicates = new List<Node>();
            for (int i = 0; i < _rbeList.Count; i++)
            {
                if (_rbeList[i].AMType == "UBOLT")
                    continue;
                depNodes1 = _rbeList[i].DepNodes;
                for (int j = i + 1; j < _rbeList.Count; j++)
                {
                    depNodes2 = _rbeList[j].DepNodes;
                    duplicates = depNodes1.Intersect(depNodes2).ToList();
                    if (duplicates.Count == 1)
                    {
                        if (depNodes1.Count >= depNodes2.Count)
                        {
                            depNodes1.Remove(duplicates[0]);
                            depNodes2.Remove(duplicates[0]);
                            depNodes1.Add(_rbeList[j].Pos);
                            _rbeList[i].DepNodes = depNodes1;
                            _rbeList[j].DepNodes = depNodes2;
                        }
                        else
                        {
                            depNodes1.Remove(duplicates[0]);
                            depNodes2.Remove(duplicates[0]);
                            depNodes2.Add(_rbeList[i].Pos);
                            _rbeList[i].DepNodes = depNodes1;
                            _rbeList[j].DepNodes = depNodes2;
                        }
                    }
                }
            }
        }

        public void RemoveDuplicatedElems()
        {
            List<Element> newElemList = _elemList.ToList();
            Prop prop1;
            Prop prop2;
            for (int i = 0; i < _elemList.Count; i++)
            {
                for (int j = i + 1; j < _elemList.Count; j++)
                {
                    if ((_elemList[i].Poss == _elemList[j].Poss && _elemList[i].Pose == _elemList[j].Pose) || (_elemList[i].Poss == _elemList[j].Pose && _elemList[i].Pose == _elemList[j].Poss))
                    {
                        prop1 = _propList.Find(s => s.PropID == _elemList[i].PropID);
                        prop2 = _propList.Find(s => s.PropID == _elemList[j].PropID);
                        if (prop1 == prop2)
                        {
                            if (newElemList.Find(s => s == _elemList[j]) != null)
                                newElemList.Remove(_elemList[j]);
                        }
                        else if (double.Parse(prop1.Dim1) >= double.Parse(prop2.Dim1))
                        {
                            if (newElemList.Find(s => s == _elemList[j]) != null)
                                newElemList.Remove(_elemList[j]);
                        }
                        else
                        {
                            if (newElemList.Find(s => s == _elemList[i]) != null)
                                newElemList.Remove(_elemList[i]);
                        }

                    }
                }
            }
            _elemList = newElemList;
        }

        /// <summary>
        /// Stru의 Node를 추출하여 리스트로 저장
        /// </summary>
        /// <param name="struList"></param>
        public void AddStruGrid(List<AMStru> struList)
        {
            List<Point3D> struPosList = new List<Point3D>();
            foreach (AMStru stru in struList)
            {
                struPosList.Add(stru.FinalPoss);
                struPosList.Add(stru.FinalPose);
                struPosList.AddRange(stru.InterPos);
            }
            struPosList = struPosList.Distinct().ToList();
            struPosList = struPosList.OrderBy(s => s.X).ThenBy(s => s.Y).ThenBy(s => s.Z).ToList();
            int id = 0;
            foreach (Point3D pos in struPosList)
            {
                if (!_struNodeList.Any(s => ModelHandle.GetDistance(s, pos) < 21))
                {
                    id += 1;
                    Node node = new Node(pos);
                    node.nodeID = id;
                    _struNodeList.Add(node);
                }
            }
            _nodeList.AddRange(_struNodeList);
        }

        /// <summary>
        /// Stru List에서 Element로 변환하여 리스트로 저장
        /// </summary>
        /// <param name="struList"></param>
        public void AddStruElem(List<AMStru> struList)
        {

            Element elem;
            List<Node> interList;
            int id = 0;
            Node node1;
            Node node2;
            // intersection 수집
            foreach (AMStru stru in struList)
            {
                interList = new List<Node>();
                node1 = GetNodeFromList(stru.FinalPoss);
                node2 = GetNodeFromList(stru.FinalPose);
                if (node1.X < 0)
                {
                    node1.X = 0;
                }
                // stru Weld정보가 있으면 해당 node를 SpcList에 추가
                if (!string.IsNullOrEmpty(stru.Weld))
                {
                    if (stru.Weld == "start")
                        SPCList.Add(node1.nodeID);
                    else if (stru.Weld == "end")
                        SPCList.Add(node2.nodeID);
                }
                interList.AddRange(GetInterNodes(stru));
                interList.AddRange(GetInterNodes1(stru));
                interList.Add(node1);
                interList.Add(node2);
                interList = interList.Distinct().ToList();
                // poss 준으로 정렬
                interList = interList.OrderBy(x => ModelHandle.GetDistance(node1, x)).ToList();
                // elem 속성 입력
                elem = new Element();
                elem.PropID = GetPropID(stru.Size, stru.Division);
                elem.Cood = stru.Ori;
                elem.AmRef = stru.Name;
                for (int i = 0; i < interList.Count() - 1; i++)
                {
                    id += 1;
                    Element newElem = new Element(elem);
                    newElem.ElemID = id;
                    newElem.Poss = interList[i];
                    newElem.Pose = interList[i + 1];
                    _elemList.Add(newElem);
                }
            }
        }
        /// <summary>
        /// ver.2409 Stru의 RBE 추가
        /// </summary>
        /// <param name="struList"></param>
        public void AddStruRbes(List<AMStru> struList)
        {
            foreach (AMStru stru in struList)
            {
                Rbe rbe = new Rbe();
                rbe.ElemID = _rbeList.Count() + 1;
                rbe.Pos = GetNodeFromStruList(stru.Poss);
                rbe.DepNodes.Add(GetNodeFromStruList(stru.Pose));
                rbe.Rest = "123456";
                rbe.AmRef = stru.Division;
                _rbeList.Add(rbe);
            }
        }

        /// <summary>
        /// Point3D값으로 Node 번호 찾기
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public Node GetNodeFromList(Point3D pos)
        {
            return _nodeList.Find(s => ModelHandle.GetDistance(s, pos) < 21);
        }
        /// <summary>
        /// Point3D값으로 struNode 번호 찾기
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public Node GetNodeFromStruList(Point3D pos)
        {
            return _struNodeList.Find(s => ModelHandle.GetDistance(s, pos) < 21);
        }
        /// <summary>
        /// Point3D값으로 pipeNode 번호 찾기
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public Node GetNodeFromPipeList(Point3D pos)
        {
            return _pipeNodeList.Find(s => ModelHandle.GetDistance(s, pos) < 21);
        }
        /// <summary>
        /// Point3D값으로 equiNode 번호 찾기
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public Node GetNodeFromEquiList(Point3D pos)
        {
            return _nodeList.Find(s => ModelHandle.GetDistance(s, pos) < 21);
        }
        /// <summary>
        /// size text로 Property 번호 찾기
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        public int GetPropID(string size, string division)
        {
            Prop prop = _propList.Find(s => s.Str == size && s.Division == division);
            return prop.PropID;
        }
        /// <summary>
        /// Pipe 객체로 Prop 번호 찾기
        /// </summary>
        /// <param name="pipe"></param>
        /// <returns></returns>
        public int GetPropID(AMPipe pipe)
        {
            string size = $"TUBE_{pipe.OutDia}x{pipe.Thick}";
            Prop prop = _propList.Find(s => s.Str == size);
            return prop.PropID;
        }
        /// <summary>
        /// Out.Dia와 Thck.로 prop 번호 찾기
        /// </summary>
        /// <param name="outDia"></param>
        /// <param name="thick"></param>
        /// <returns></returns>
        public int GetPropID(double outDia, double thick)
        {
            string size = $"TUBE_{outDia}x{thick}";
            Prop prop = _propList.Find(s => s.Str == size && s.Division == "PIPE");
            return prop.PropID;
        }
        /// <summary>
        /// Stru 객체의 양끝점을 기준으로 사이에 있는 모든 Node 찾기
        /// </summary>
        /// <param name="stru"></param>
        /// <returns></returns>
        public List<Node> GetInterNodes(AMStru stru)
        {
            List<Node> interList = new List<Node>();

            foreach (var pos in stru.InterPos)
            {
                interList.Add(GetNodeFromList(pos));
            }
            return interList;
        }
        /// <summary>
        /// Stru 객체의 양끝점을 기준으로 사이에 있는 모든 Node 찾기
        /// </summary>
        /// <param name="stru"></param>
        /// <returns></returns>
        public List<Node> GetInterNodes1(AMStru stru)
        {
            List<Node> interList = new List<Node>();
            double lineLength = ModelHandle.GetDistance(stru.Poss, stru.Pose);
            double distanceStartToPoint = 0;
            double distanceEndToPoint = 0;
            double tolerance = 0.1;
            foreach (Node node in _struNodeList)
            {
                distanceStartToPoint = ModelHandle.GetDistance(node.X, node.Y, node.Z, stru.Poss.X, stru.Poss.Y, stru.Poss.Z);
                distanceEndToPoint = ModelHandle.GetDistance(node.X, node.Y, node.Z, stru.Pose.X, stru.Pose.Y, stru.Pose.Z);
                if (Math.Abs(distanceStartToPoint + distanceEndToPoint - lineLength) < tolerance)
                {
                    interList.Add(node);
                }
            }
            return interList;
        }
        /// <summary>
        /// Pipe 객체(Tubi)의 양 끝점을 기준으로 사이에 있는 모든 Node 찾기
        /// </summary>
        /// <param name="pipe"></param>
        /// <returns></returns>
        public List<Node> GetInterNodes(AMPipe pipe)
        {
            List<Node> interList = new List<Node>();
            double lineLength = ModelHandle.GetDistance(pipe.APos, pipe.LPos);
            double distanceStartToPoint = 0;
            double distanceEndToPoint = 0;
            double tolerance = 0.1;
            foreach (Node node in _pipeNodeList)
            {
                distanceStartToPoint = ModelHandle.GetDistance(node.X, node.Y, node.Z, pipe.APos.X, pipe.APos.Y, pipe.APos.Z);
                distanceEndToPoint = ModelHandle.GetDistance(node.X, node.Y, node.Z, pipe.LPos.X, pipe.LPos.Y, pipe.LPos.Z);
                if (Math.Abs(distanceStartToPoint + distanceEndToPoint - lineLength) < tolerance)
                {
                    interList.Add(node);
                }
            }
            return interList;
        }

        /// <summary>
        /// Eqiupment 리스트를 Node 및 RBE, Mass로 변환하여 리스트에 추가 
        /// </summary>
        /// <param name="equiList"></param>
        public void AddEquiGridElement(List<AMEqui> equiList)
        {
            int id;
            List<Node> tempNodes;
            Node tempNode;
            foreach (AMEqui equi in equiList)
            {
                if (equi.Mass == 0)
                    continue;
                if (!_nodeList.Any(s => ModelHandle.GetDistance(s, equi.Cog) < 21))
                {
                    id = _nodeList.Count() + 1;
                    Node node = new Node(equi.Cog);
                    node.nodeID = id;
                    _nodeList.Add(node);
                }
                Mass mass = new Mass();
                mass.ElemID = _massList.Count() + 1;
                mass.Pos = GetNodeFromEquiList(equi.Cog);
                mass.massVal = equi.Mass;
                mass.AmRef = equi.Name;
                _massList.Add(mass);

                tempNodes = new List<Node>();
                foreach (Point3D pos in equi.DepPos)
                {
                    tempNode = GetNodeFromList(pos);
                    if (tempNode == null)
                        continue;
                    tempNodes.Add(tempNode);
                }
                if (tempNodes.Count() > 0)
                {
                    Rbe rbe = new Rbe();
                    rbe.ElemID = _rbeList.Count() + 1;
                    rbe.Pos = GetNodeFromEquiList(equi.Cog);
                    rbe.DepNodes.AddRange(tempNodes);
                    rbe.Rest = "123456";
                    rbe.AmRef = equi.Name;
                    _rbeList.Add(rbe);
                }
            }
        }

        /// <summary>
        /// Stru 리스트에서 Property를 찾아서 추가
        /// </summary>
        /// <param name="struList"></param>
        public void AddProp(List<AMStru> struList)
        {
            List<AMStru> suppList = new List<AMStru>();
            List<AMStru> outfList = new List<AMStru>();
            foreach (var stru in struList)
            {
                if (stru.Division == "SUPP")
                    suppList.Add(stru);
                else
                    outfList.Add(stru);
            }
            // support
            List<string> sizeSuppStrList = new List<string>();
            suppList.ForEach(s => sizeSuppStrList.Add(s.Size));
            sizeSuppStrList = sizeSuppStrList.Distinct().ToList();
            int id = 0;
            foreach (string prop in sizeSuppStrList)
            {
                id += 1;
                Prop newProp = new Prop(id, prop,"SUPP");
                _propList.Add(newProp);
            }
            // 철의장
            List<string> sizeOutfStrList = new List<string>();
            outfList.ForEach(s => sizeOutfStrList.Add(s.Size));
            sizeOutfStrList = sizeOutfStrList.Distinct().ToList();
            id = 50;
            if (_propList.Count > 50)
                id = 550;
            foreach (string prop in sizeOutfStrList)
            {
                id += 1;
                Prop newProp = new Prop(id, prop, "OUTF");
                _propList.Add(newProp);
            }

        }
        /// <summary>
        /// Pipe 리스트에서 Property를 찾아서 추가
        /// </summary>
        /// <param name="pipeList"></param>
        //public void AddProp1(List<AMPipe> pipeList)
        //{
        //    List<string> sizeStrList = new List<string>();
        //    foreach (AMPipe pipe in pipeList)
        //    {
        //        if (new List<string> { "TUBI", "REDU", "TEE", "BEND", "ELBO", "COUP", "OLET" }.Exists(s => s == pipe.Type))
        //            sizeStrList.Add($"TUBE_{pipe.OutDia}x{pipe.Thick}");
        //        if (pipe.Type == "TEE")
        //            sizeStrList.Add($"TUBE_{pipe.OutDia2}x{pipe.Thick2}");
        //        if (pipe.Type == "FLAN" && pipe.APos != pipe.LPos && pipe.OutDia != 0)
        //            sizeStrList.Add($"TUBE_{pipe.OutDia}x{pipe.Thick}");
        //    }
        //    sizeStrList = sizeStrList.Distinct().ToList();
        //    int id = _propList.Count();
        //    foreach (string size in sizeStrList)
        //    {
        //        if (_propList.Any(s => s.Str == size))
        //            continue;
        //        id += 1;
        //        Prop newProp = new Prop(id, size);
        //        _propList.Add(newProp);
        //    }
        //}
        /// <summary>
        /// Pipe Property 별도 번호 부여
        /// </summary>
        /// <param name="pipeList"></param>
        public void AddProp(List<AMPipe> pipeList, bool isFluidDensity)
        {
            List<string> sizeStrList = new List<string>();
            List<Prop> pipeProps = new List<Prop>();
            foreach (AMPipe pipe in pipeList)
            {
                if (new List<string> { "TUBI", "REDU", "TEE", "BEND", "ELBO", "COUP", "OLET" }.Exists(s => s == pipe.Type))
                    sizeStrList.Add($"TUBE_{pipe.OutDia}x{pipe.Thick}");
                if (pipe.Type == "TEE")
                    sizeStrList.Add($"TUBE_{pipe.OutDia2}x{pipe.Thick2}");
                if (pipe.Type == "FLAN" && pipe.APos != pipe.LPos && pipe.OutDia != 0)
                    sizeStrList.Add($"TUBE_{pipe.OutDia}x{pipe.Thick}");
            }
            sizeStrList = sizeStrList.Distinct().ToList();
            // 100부터 시작, 이미 100보다 크면 1000부터 시작
            int id = _propList.Count() < 100 ? 100 : 1000;
            int matId = 1;
            Prop newProp;
            foreach (string size in sizeStrList)
            {
                if (pipeProps.Any(s => s.Str == size))
                    continue;
                id += 1;
                matId += 1;
                if (isFluidDensity)
                {
                    Mat mat = new Mat(matId, size);
                    MatList.Add(mat);
                    newProp = new Prop(id, size, matId);
                }
                else
                {
                    newProp = new Prop(id, size, "PIPE");
                }

                _propList.Add(newProp);
            }
        }

        /// <summary>
        /// Mesh Size에 맞게 Element를 쪼갠다.
        /// </summary>
        /// <param name="meshSize"></param>
        public void Meshing(double meshSize)
        {
            List<Element> newElemList = new List<Element>();
            List<Node> newNodeList = _nodeList.ToList();
            List<Node> tempNodeList;
            int step;
            double dx;
            double dy;
            double dz;
            int elemId = _elemList.Count();
            int nodeId = _nodeList.Count();
            foreach (Element elem in _elemList)
            {
                step = (int)(ModelHandle.GetDistance(elem.Poss, elem.Pose) / meshSize);
                if ((int)(ModelHandle.GetDistance(elem.Poss, elem.Pose) / (meshSize * 1.3)) > 0)
                {
                    tempNodeList = new List<Node>();
                    tempNodeList.Add(elem.Poss);
                    dx = (elem.Pose.X - elem.Poss.X) / (step + 1);
                    dy = (elem.Pose.Y - elem.Poss.Y) / (step + 1);
                    dz = (elem.Pose.Z - elem.Poss.Z) / (step + 1);
                    for (int i = 1; i <= step; i++)
                    {
                        Node newNode = new Node(elem.Poss.X + i * dx, elem.Poss.Y + i * dy, elem.Poss.Z + i * dz);
                        nodeId += 1;
                        newNode.nodeID = nodeId;
                        newNodeList.Add(newNode);
                        tempNodeList.Add(newNode);
                    }
                    tempNodeList.Add(elem.Pose);

                    for (int i = 0; i < tempNodeList.Count() - 1; i++)
                    {
                        elemId += 1;
                        Element newElem = new Element(elem);
                        newElem.ElemID = elemId;
                        newElem.Poss = tempNodeList[i];
                        newElem.Pose = tempNodeList[i + 1];
                        newElemList.Add(newElem);
                    }
                }
                else
                    newElemList.Add(elem);
            }
            _nodeList = newNodeList;
            _elemList = newElemList;
            Renumbering();
        }
    }
}
