﻿using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace MySpace.DataRelay.RelayComponent.Forwarding
{
	/// <summary>
	/// A class that stores the statistical information collected by the <see cref="Forwarder"/>.
	/// </summary>
	[XmlRoot("ForwarderStatus")]
    public class ForwarderStatus
	{
		/// <summary>
		/// Gets or sets statistical information specific to the <see cref="Forwarder"/>. 
		/// </summary>
		[XmlElement("RelayStatistics")]
		public RelayStatistics RelayStatistics { set; get; }
		
		/// <summary>
		/// Gets Statistical information collected by the <see cref="Forwarder"/> that is specific 
		/// to a <see cref="NodeGroup"/>; Never <see langword="null"/>.
		/// </summary>
		public List<NodeGroupStatus> NodeGroupStatuses
		{
			get
			{
				return _nodeGroupStatuses;
			}
		}
		[XmlElement("NodeGroupStatuses")]
		readonly private List<NodeGroupStatus> _nodeGroupStatuses = new List<NodeGroupStatus>();
	}
}