﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using System.Diagnostics;
using OpenCL.Net;

namespace JaNet
{
    class NeuralNetwork
    {
        #region NeuralNetwork class fields (private)

        private List<Layer> layers;
        private int nLayers;

        #endregion


        #region NeuralNetwork class properties (public)

        public List<Layer> Layers
        {
            get { return layers; }
        }
        public int NumberOfLayers
        {
            get { return nLayers; }
        }

        #endregion


        #region Setup methods (to be called once)

        /// <summary>
        /// NeuralNetwork class constructor.
        /// </summary>
        public NeuralNetwork()
        {
            //Console.WriteLine("--- New empty network created ---\n");
            this.layers = new List<Layer>(); // empty list of layers
            this.nLayers = 0;
        }

        /// <summary>
        /// Add layer to NeuralNetwork object.
        /// </summary>
        /// <param name="layer"></param>
        public void AddLayer(Layer layer)
        {
            layer.ID = nLayers;
            if (this.layers.Any()) // if layer list is not empty
                this.layers.Last().NextLayer = layer; // set this layer as layer field of previous one

            this.layers.Add(layer);
            this.nLayers++;
        }

        /// <summary>
        /// Setup network: given input dim and each layer's parameters, automatically set dimensions of I/O 3D arrays and initialize weights and biases.
        /// </summary>
        /// <param name="inputDimensions"></param>
        /// <param name="nOutputClasses"></param>
        public void Setup(int inputWidth, int inputHeigth, int inputDepth, int nOutputClasses)
        {
            Console.WriteLine("\n=========================================");
            Console.WriteLine("    Network setup and initialization");
            Console.WriteLine("=========================================\n");

            Console.WriteLine("Setting up layer 0 (input layer): " + layers[0].Type);
            layers[0].SetAsFirstLayer(inputWidth, inputHeigth, inputDepth); 
            layers[0].InitializeParameters();

            for (int i = 1; i < layers.Count; i++ ) // all other layers
            {
                Console.WriteLine("Setting up layer " + i.ToString() + ": " + layers[i].Type);
                layers[i].ConnectTo(layers[i - 1]);
                layers[i].InitializeParameters();
                
            }
        }

        #endregion


        #region Training methods

        public void FeedData(DataSet dataSet, int iDataPoint)
        {
#if OPENCL_ENABLED
            layers[0].Input.ActivationsGPU = dataSet.DataGPU(iDataPoint); // Copied by reference
#else
            layers[0].Input.SetHost(dataSet.GetDataPoint(iDataPoint));
#endif
        }


        public void ForwardPass()
        {
            //TODO: generalise to miniBatchSize > 1

            // Run network forward
            for (int l = 0; l < nLayers; l++)
            {

#if DEBUGGING_STEPBYSTEP
                /* ------------------------- DEBUGGING --------------------------------------------- */

                // Display input layer-by-layer

                float[] layerInput = new float[layers[l].Input.NumberOfUnits];
#if OPENCL_ENABLED
                CL.Error = Cl.EnqueueReadBuffer(CL.Queue,
                                                layers[l].Input.ActivationsGPU, // source
                                                Bool.True,
                                                (IntPtr)0,
                                                (IntPtr)(layers[l].Input.NumberOfUnits * sizeof(float)),
                                                layerInput,  // destination
                                                0,
                                                null,
                                                out CL.Event);
                CL.CheckErr(CL.Error, "NeuralNetwork.ForwardPass Cl.clEnqueueReadBuffer layerInput");
#else
                layerInput = layers[l].Input.GetHost();
#endif
                Console.WriteLine("\nLayer {0} ({1}) input activations:",l , layers[l].Type);
                for (int j = 0; j < layerInput.Length; j++)
                    Console.Write("{0}  ", layerInput[j]);
                Console.WriteLine();
                Console.ReadKey();


                /* ------------------------- END DEBUGGING --------------------------------------------- */
#endif

                layers[l].FeedForward();

#if DEBUGGING_STEPBYSTEP
                /* ------------------------- DEBUGGING --------------------------------------------- */

                // Display output layer-by-layer

                float[] layerOutput = new float[layers[l].Output.NumberOfUnits];
#if OPENCL_ENABLED
                CL.Error = Cl.EnqueueReadBuffer(CL.Queue,
                                                layers[l].Output.ActivationsGPU, // source
                                                Bool.True,
                                                (IntPtr)0,
                                                (IntPtr)(layers[l].Output.NumberOfUnits * sizeof(float)),
                                                layerOutput,  // destination
                                                0,
                                                null,
                                                out CL.Event);
                CL.CheckErr(CL.Error, "NeuralNetwork.ForwardPass Cl.clEnqueueReadBuffer layerOutput");
#else
                layerOutput = layers[l].Output.GetHost();
#endif
                Console.WriteLine("\nLayer {0} ({1}) output activations:", l, layers[l].Type);
                for (int j = 0; j < layerOutput.Length; j++)
                        Console.Write("{0}  ", layerOutput[j]);
                Console.WriteLine();
                Console.ReadKey();


                /* ------------------------- END DEBUGGING --------------------------------------------- */
#endif

            }
        }

        /// <summary>
        /// Run network backwards, propagating the gradient backwards and also updating parameters. 
        /// Requires that gradient has ALREADY BEEN WRITTEN in network.Layers[nLayers-1].Input.Delta
        /// </summary>
        public void BackwardPass(double learningRate, double momentumMultiplier)
        {
            for (int l = nLayers - 2; l >= 0; l--) // propagate error signal backwards (layers L-2 to 0)
            {

#if DEBUGGING_STEPBYSTEP
                /* ------------------------- DEBUGGING --------------------------------------------- */

                // Display output layer-by-layer
                float[] deltaOutput = new float[layers[l].Output.NumberOfUnits];
#if OPENCL_ENABLED
                CL.Error = Cl.EnqueueReadBuffer(CL.Queue,
                                                layers[l].Output.DeltaGPU, // source
                                                Bool.True,
                                                (IntPtr)0,
                                                (IntPtr)(layers[l].Output.NumberOfUnits * sizeof(float)),
                                                deltaOutput,  // destination
                                                0,
                                                null,
                                                out CL.Event);
                CL.CheckErr(CL.Error, "NeuralNetwork.BackwardPass Cl.clEnqueueReadBuffer deltaOutput");
#else
                deltaOutput = layers[l].Output.DeltaHost;
#endif
                Console.WriteLine("\nLayer {0} ({1}) output delta:", l, layers[l].Type);
                for (int j = 0; j < deltaOutput.Length; j++)
                    Console.Write("{0}  ", deltaOutput[j]);
                Console.WriteLine();
                Console.ReadKey();


                /* ------------------------- END DEBUGGING --------------------------------------------- */
#endif
                if (l > 0) // no need to backprop first layer
                {
                    layers[l].BackPropagate();
                }

#if DEBUGGING_STEPBYSTEP
                /* ------------------------- DEBUGGING --------------------------------------------- */

                // Display output layer-by-layer
                float[] deltaInput = new float[layers[l].Input.NumberOfUnits];
#if OPENCL_ENABLED
                CL.Error = Cl.EnqueueReadBuffer(CL.Queue,
                                                layers[l].Input.DeltaGPU, // source
                                                Bool.True,
                                                (IntPtr)0,
                                                (IntPtr)(layers[l].Input.NumberOfUnits * sizeof(float)),
                                                deltaInput,  // destination
                                                0,
                                                null,
                                                out CL.Event);
                CL.CheckErr(CL.Error, "NeuralNetwork.BackwardPass Cl.clEnqueueReadBuffer deltaInput");
#else
                deltaInput = layers[l].Input.DeltaHost;
#endif
                Console.WriteLine("\nLayer {0} ({1}) input delta:", l, layers[l].Type);
                for (int j = 0; j < deltaInput.Length; j++)
                    Console.Write("{0}  ", deltaInput[j]);
                Console.WriteLine();
                Console.ReadKey();


                /* ------------------------- END DEBUGGING --------------------------------------------- */
#endif
                layers[l].UpdateParameters(learningRate, momentumMultiplier);
            }
        }

        #endregion

    } 
}