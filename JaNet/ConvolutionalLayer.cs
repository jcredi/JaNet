﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenCL.Net;


namespace JaNet
{
    class ConvolutionalLayer : Layer
    {
        
        #region Fields (private)

        private int filterSize; // F
        private int nFilters; // K
        private int strideLength; // S
        private int zeroPadding; // P
        private int receptiveFieldSize; // i.e. [outputDepth * filterSize^2]
        private int nReceptiveFields; // i.e. output depth

        private int paddedInputSize;
        private int receptiveFieldsLookupTableSize;

#if OPENCL_ENABLED
        
        private Mem paddedInputGPU;
        private Mem receptiveFieldsLookupTableGPU;

        private Mem weightsGPU;
        private Mem biasesGPU;

        private Mem weightsUpdateSpeedGPU;
        private Mem biasesUpdateSpeedGPU;

        // Global and local work-group sizes (for OpenCL kernels) - will be set in SetWorkGroupSizes();

        private IntPtr[] paddingGlobalWorkSizePtr;
        private IntPtr[] paddingLocalWorkSizePtr;

        private IntPtr[] im2colGlobalWorkSizePtr;
        private IntPtr[] im2colLocalWorkSizePtr;

        private IntPtr[] forwardGlobalWorkSizePtr;
        private IntPtr[] forwardLocalWorkSizePtr;

        private IntPtr[] backwardGlobalWorkSizePtr;
        private IntPtr[] backwardLocalWorkSizePtr;

        private IntPtr[] weightsGradientGlobalWorkSizePtr;
        private IntPtr[] weightsGradientLocalWorkSizePtr;

        private IntPtr[] biasesGradientGlobalWorkSizePtr;
        private IntPtr[] biasesGradientLocalWorkSizePtr;

        private IntPtr[] updateParametersGlobalWorkSizePtr;
        private IntPtr[] updateParametersLocalWorkSizePtr;

#else
        private float[] paddedInput; // dimension [inputD * (inputH + 2*padding) * (inutW + 2*padding)]
        //private float[] paddedOutput; // dimension [inputD * (inputH + filterSize - 1) * (inutW + filterSize - 1)] <- this makes sure that backprop works

        private float[,] receptiveFieldsLookupTable; // dimension [receptiveFieldSize , nReceptiveFields] = [inputDepth*filterSize^2 , outputWidth*outputHeight]
        private float[,] outputMatrix; // dimension [numberOfFilters , outputWidth*outputHeight]

        private float[,] weights; // dimension [nFilters , inputDepth*filterSize^2]
        private float[] biases; // dimension [nFilters , 1]

        private float[,] weightsUpdateSpeed; // dimension [nFilters , inputDepth*filterSize^2]
        private float[] biasesUpdateSpeed; // dimension [nFilters , 1]
#endif

        #endregion


        #region Properties (public)

        #endregion


        #region Setup methods (to be called once)

        /// <summary>
        /// Constructor: specify filter size, number of filters (output depth), stride (only 1 supported at this stage!), zero padding.
        /// </summary>
        /// <param name="FilterSize"></param>
        /// <param name="nOfFilters"></param>
        /// <param name="StrideLength"></param>
        /// <param name="ZeroPadding"></param>
        public ConvolutionalLayer(int FilterSize, int nOfFilters, int StrideLength, int ZeroPadding)
        {
            this.type = "Convolutional";

            if (FilterSize % 2 != 1)
                throw new ArgumentException("Only odd filter size is supported.");
            this.filterSize = FilterSize;
            this.nFilters = nOfFilters;
            this.strideLength = StrideLength;
            this.zeroPadding = ZeroPadding;
        }


        public override void ConnectTo(Layer PreviousLayer)
        {
            // Setup input
            base.ConnectTo(PreviousLayer);

            if (PreviousLayer.OutputHeight != PreviousLayer.OutputWidth)
                throw new ArgumentException("ConvolutionalLayer currently only supports square input (spatially).");

            this.inputWidth = PreviousLayer.OutputWidth;
            this.inputHeight = PreviousLayer.OutputHeight;
            this.inputDepth = PreviousLayer.OutputDepth;

            // Setup output
            double tmp = (double)(inputWidth - filterSize + 2 * zeroPadding) / (double)strideLength + 1; // then check if this number is int
            if (Math.Abs(tmp % 1) > Global.EPSILON) 
                throw new System.ArgumentException("Input width, filter size, zero padding and stride length do not fit well. Use different values");
            this.outputWidth = (int)tmp;
            this.outputHeight = (int)tmp;
            this.nReceptiveFields = outputHeight * outputWidth;

            this.outputDepth = nFilters;
            this.receptiveFieldSize = inputDepth * filterSize * filterSize;

            this.outputNeurons = new Neurons(outputDepth * outputWidth * outputHeight);

            this.paddedInputSize = inputDepth * (inputHeight + 2 * zeroPadding) * (inputWidth + 2 * zeroPadding);
            this.receptiveFieldsLookupTableSize = receptiveFieldSize * nReceptiveFields;

#if OPENCL_ENABLED

            // Padded input

            this.paddedInputGPU = (Mem)Cl.CreateBuffer( OpenCLSpace.Context, 
                                                        MemFlags.ReadWrite,
                                                        (IntPtr)(sizeof(float) * paddedInputSize),
                                                        out OpenCLSpace.ClError);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "ConnectTo(): Cl.CreateBuffer paddedInputGPU");

            // Receptive fields lookup table

            this.receptiveFieldsLookupTableGPU = (Mem)Cl.CreateBuffer(  OpenCLSpace.Context,
                                                                        MemFlags.ReadWrite,
                                                                        (IntPtr)(sizeof(int) * receptiveFieldsLookupTableSize),
                                                                        out OpenCLSpace.ClError);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "ConnectTo(): Cl.CreateBuffer receptiveFieldsLookupTableGPU");

            

            // (no need for output matrix: will be written directly to OuptutNeurons.ActivationsGPU
#else
            // Cpu code

            this.paddedInput = new float[paddedInputSize];
            this.receptiveFieldsLookupTable = new float[receptiveFieldSize, nReceptiveFields];
            this.outputMatrix = new float[nFilters, nReceptiveFields];
#endif

            // Set all work group sizes
#if OPENCL_ENABLED

            SetWorkGroupSizes();

            // We're ready to create the lookup table once and for all

            // Set kernel arguments
            OpenCLSpace.ClError = Cl.SetKernelArg(OpenCLSpace.Im2colLookupTable, 0, receptiveFieldsLookupTableGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.Im2colLookupTable, 1, (IntPtr)sizeof(int), inputWidth);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.Im2colLookupTable, 2, (IntPtr)sizeof(int), outputWidth);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.Im2colLookupTable, 3, (IntPtr)sizeof(int), filterSize);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.Im2colLookupTable, 4, (IntPtr)sizeof(int), receptiveFieldSize);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.Im2colLookupTable, 5, (IntPtr)sizeof(int), strideLength);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "ConnectTo(): Cl.SetKernelArg Im2colLookupTable");

            // Run kernel
            OpenCLSpace.ClError = Cl.EnqueueNDRangeKernel(  OpenCLSpace.Queue,
                                                            OpenCLSpace.Im2colLookupTable,
                                                            2,
                                                            null,
                                                            im2colGlobalWorkSizePtr,
                                                            im2colLocalWorkSizePtr,
                                                            0,
                                                            null,
                                                            out OpenCLSpace.ClEvent);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "ConvolutionalLayer.ConnectTo() Im2colLookupTable() Cl.EnqueueNDRangeKernel");

            OpenCLSpace.ClError = Cl.Finish(OpenCLSpace.Queue);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Cl.Finish");

            OpenCLSpace.ClError = Cl.ReleaseEvent(OpenCLSpace.ClEvent);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Cl.ReleaseEvent");

#endif
        }

#if OPENCL_ENABLED
        private void SetWorkGroupSizes()
        {
            // Work group sizes will be set as follows:
            //      global work size = smallest multiple of WAVEFRONT larger than the total number of processes needed (for efficiency)
            //      local work size = WAVEFRONT or small multiples of it (making sure that global worksize is a multiple of this)
            // WAVEFRONT is a constant multiple of 2. Suggested values: 32 (Nvidia) or 64 (AMD).


            // TODO: also make sure that each local work group size is lesser than KERNEL_WORK_GROUP_SIZE

            // Zero padding / unpadding (1D workspace) _________________________________________________________________________________
            int inputSize = inputDepth * inputWidth * inputHeight;
            int smallestMultipleInputSize = (int)(OpenCLSpace.WAVEFRONT * Math.Ceiling((double)(inputSize) / (double)OpenCLSpace.WAVEFRONT));
            this.paddingGlobalWorkSizePtr = new IntPtr[] {(IntPtr)smallestMultipleInputSize};
            int localWorkSize = OpenCLSpace.WAVEFRONT;
            int maxKernelWorkGroupSize = Cl.GetKernelWorkGroupInfo( OpenCLSpace.ZeroPad, 
                                                                    OpenCLSpace.Device, 
                                                                    KernelWorkGroupInfo.WorkGroupSize, 
                                                                    out OpenCLSpace.ClError).CastTo<int>();
            while (localWorkSize <= OpenCLSpace.MaxWorkGroupSize && localWorkSize <= maxKernelWorkGroupSize)
            {
                int tmpLocalWorkSize = 2*localWorkSize;
                if (smallestMultipleInputSize % tmpLocalWorkSize == 0) // if global divides local
                    localWorkSize = tmpLocalWorkSize;
                else
                    break;
            }
            this.paddingLocalWorkSizePtr = new IntPtr[] { (IntPtr)localWorkSize };
 

            // Receptive field lookup table (2D workspace) _____________________________________________________________________________
            IntPtr smallestMultipleReceptiveFieldSize = (IntPtr)(OpenCLSpace.WAVEFRONT * Math.Ceiling((double)(receptiveFieldSize) / (double)OpenCLSpace.WAVEFRONT));
            IntPtr smallestMultipleNReceptiveFields = (IntPtr)(OpenCLSpace.WAVEFRONT * Math.Ceiling((double)(nReceptiveFields) / (double)OpenCLSpace.WAVEFRONT));
            this.im2colGlobalWorkSizePtr = new IntPtr[] { smallestMultipleReceptiveFieldSize, smallestMultipleNReceptiveFields };
            this.im2colLocalWorkSizePtr = new IntPtr[] { (IntPtr)(OpenCLSpace.WAVEFRONT/4), (IntPtr)(OpenCLSpace.WAVEFRONT/2) };

            // Forward kernel (2D workspace) ___________________________________________________________________________________________
            IntPtr smallestMultipleOutputDepth = (IntPtr)(OpenCLSpace.WAVEFRONT * Math.Ceiling((double)(outputDepth) / (double)OpenCLSpace.WAVEFRONT));
            this.forwardGlobalWorkSizePtr = new IntPtr[] { smallestMultipleOutputDepth, smallestMultipleNReceptiveFields };
            this.forwardLocalWorkSizePtr = new IntPtr[] { (IntPtr)(OpenCLSpace.WAVEFRONT / 4), (IntPtr)(OpenCLSpace.WAVEFRONT / 2) };

            // Backward kernel (2D workspace) ___________________________________________________________________________________________
            this.backwardGlobalWorkSizePtr = new IntPtr[] { smallestMultipleReceptiveFieldSize, smallestMultipleNReceptiveFields };
            this.backwardLocalWorkSizePtr = new IntPtr[] { (IntPtr)(OpenCLSpace.WAVEFRONT / 4), (IntPtr)(OpenCLSpace.WAVEFRONT / 2) };

            // Weights gradient kernel (2D workspace) ___________________________________________________________________________________
            this.weightsGradientGlobalWorkSizePtr = new IntPtr[] { smallestMultipleOutputDepth, smallestMultipleReceptiveFieldSize };
            this.weightsGradientLocalWorkSizePtr = new IntPtr[] { (IntPtr)(OpenCLSpace.WAVEFRONT / 4), (IntPtr)(OpenCLSpace.WAVEFRONT / 2) };

            // Biases gradient kernel (1D workspace) ___________________________________________________________________________________
            this.biasesGradientGlobalWorkSizePtr = new IntPtr[] { smallestMultipleOutputDepth };
            this.biasesGradientLocalWorkSizePtr = new IntPtr[] { (IntPtr)(OpenCLSpace.WAVEFRONT * 2)};

            // Weights gradient kernel (2D workspace) ___________________________________________________________________________________
            this.updateParametersGlobalWorkSizePtr = new IntPtr[] { smallestMultipleOutputDepth, smallestMultipleReceptiveFieldSize };
            this.updateParametersLocalWorkSizePtr = new IntPtr[] { (IntPtr)(OpenCLSpace.WAVEFRONT / 4), (IntPtr)(OpenCLSpace.WAVEFRONT / 2) };
        }
#endif


        public override void InitializeParameters()
        {
            // Initialize weigths as normally distributed numbers with mean 0 and std equals to 1/sqrt(numberOfInputUnits)
            // Initialize biases as small positive numbers, e.g. 0.01

            float[,] initWeights = new float[nFilters, receptiveFieldSize];
            float[] initBiases = new float[nFilters];

            float[,] initWeightsUpdateSpeed = new float[nFilters, receptiveFieldSize]; // zeros
            float[] initBiasesUpdateSpeed = new float[nFilters]; // zeros

            double weightsStdDev = Math.Sqrt(2.0 / this.inputNeurons.NumberOfUnits);
            double uniformRand1;
            double uniformRand2;
            double tmp;

            for (int iRow = 0; iRow < initWeights.GetLength(0); iRow++)
            {
                for (int iCol = 0; iCol < initWeights.GetLength(1); iCol++)
                {
                    uniformRand1 = Global.rng.NextDouble();
                    uniformRand2 = Global.rng.NextDouble();
                    // Use a Box-Muller transform to get a random normal(0,1)
                    tmp = Math.Sqrt(-2.0 * Math.Log(uniformRand1)) * Math.Sin(2.0 * Math.PI * uniformRand2);
                    tmp = weightsStdDev * tmp; // rescale using stdDev

                    initWeights[iRow, iCol] = (float)tmp;
                }

                initBiases[iRow] = 0.01F;
            }


#if OPENCL_ENABLED
            // initialize parameter buffers and write these random initial values to them

            int weightBufferSize = sizeof(float) * (nFilters * receptiveFieldSize);
            int biasesBufferSize = sizeof(float) * nFilters;

            this.weightsGPU = (Mem)Cl.CreateBuffer( OpenCLSpace.Context,
                                                    MemFlags.ReadWrite | MemFlags.CopyHostPtr,
                                                    (IntPtr)weightBufferSize,
                                                    initWeights,
                                                    out OpenCLSpace.ClError);
            this.biasesGPU = (Mem)Cl.CreateBuffer(  OpenCLSpace.Context,
                                                    MemFlags.ReadWrite | MemFlags.CopyHostPtr,
                                                    (IntPtr)biasesBufferSize,
                                                    initBiases,
                                                    out OpenCLSpace.ClError);

            // Also initialize update speeds (to zero)

            this.weightsUpdateSpeedGPU = (Mem)Cl.CreateBuffer(  OpenCLSpace.Context,
                                                                MemFlags.ReadWrite | MemFlags.CopyHostPtr,
                                                                (IntPtr)weightBufferSize,
                                                                initWeightsUpdateSpeed,
                                                                out OpenCLSpace.ClError);
            this.biasesUpdateSpeedGPU = (Mem)Cl.CreateBuffer(   OpenCLSpace.Context,
                                                                MemFlags.ReadWrite | MemFlags.CopyHostPtr,
                                                                (IntPtr)biasesBufferSize,
                                                                initBiasesUpdateSpeed,
                                                                out OpenCLSpace.ClError);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "InitializeParameters(): Cl.CreateBuffer");
#else

            this.weights = initWeights;
            this.biases = initBiases;

            this.weightsUpdateSpeed = initWeightsUpdateSpeed;
            this.biasesUpdateSpeed = initBiasesUpdateSpeed;
#endif
        }

        #endregion


        #region Training methods

        public override void FeedForward()
        {
#if OPENCL_ENABLED
            
            // 1. Zero-pad input tensor _________________________________________________________

            // Set kernel arguments
            OpenCLSpace.ClError = Cl.SetKernelArg(OpenCLSpace.ZeroPad, 0, inputNeurons.ActivationsGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ZeroPad, 1, paddedInputGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ZeroPad, 2, (IntPtr)sizeof(int), inputWidth);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ZeroPad, 3, (IntPtr)sizeof(int), inputWidth * inputHeight);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ZeroPad, 4, (IntPtr)sizeof(int), inputWidth * inputHeight * inputDepth);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ZeroPad, 5, (IntPtr)sizeof(int), zeroPadding);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ZeroPad, 6, (IntPtr)sizeof(int), zeroPadding * (2 * zeroPadding + inputWidth));
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ZeroPad, 7, (IntPtr)sizeof(int), 2 * zeroPadding * (inputWidth + inputHeight + 2 * zeroPadding));
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Convolutional.FeedForward(): Cl.SetKernelArg ZeroPad");

            // Run kernel
            OpenCLSpace.ClError = Cl.EnqueueNDRangeKernel(  OpenCLSpace.Queue,
                                                            OpenCLSpace.ZeroPad,
                                                            1,
                                                            null,
                                                            paddingGlobalWorkSizePtr,
                                                            paddingLocalWorkSizePtr,
                                                            0,
                                                            null,
                                                            out OpenCLSpace.ClEvent);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Convolutional.FeedForward(): Cl.EnqueueNDRangeKernel ZeroPad");

            OpenCLSpace.ClError = Cl.Finish(OpenCLSpace.Queue);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Cl.Finish");

            OpenCLSpace.ClError = Cl.ReleaseEvent(OpenCLSpace.ClEvent);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Cl.ReleaseEvent");

            // 2. Convolve input and filters _________________________________________________________
            
            // Set kernel arguments
            OpenCLSpace.ClError = Cl.SetKernelArg(OpenCLSpace.ConvForward, 0, outputNeurons.ActivationsGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ConvForward, 1, paddedInputGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ConvForward, 2, receptiveFieldsLookupTableGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ConvForward, 3, weightsGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ConvForward, 4, biasesGPU);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ConvForward, 5, (IntPtr)sizeof(int), nFilters);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ConvForward, 6, (IntPtr)sizeof(int), receptiveFieldSize);
            OpenCLSpace.ClError |= Cl.SetKernelArg(OpenCLSpace.ConvForward, 7, (IntPtr)sizeof(int), nReceptiveFields);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Convolutional.FeedForward(): Cl.SetKernelArg ConvForward");

            // Run kernel
            OpenCLSpace.ClError = Cl.EnqueueNDRangeKernel(  OpenCLSpace.Queue,
                                                            OpenCLSpace.ConvForward,
                                                            2,
                                                            null,
                                                            forwardGlobalWorkSizePtr,
                                                            forwardLocalWorkSizePtr,
                                                            0,
                                                            null,
                                                            out OpenCLSpace.ClEvent);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Convolutional.FeedForward(): Cl.EnqueueNDRangeKernel ConvForward");

            OpenCLSpace.ClError = Cl.Finish(OpenCLSpace.Queue);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Cl.Finish");

            OpenCLSpace.ClError = Cl.ReleaseEvent(OpenCLSpace.ClEvent);
            OpenCLSpace.CheckErr(OpenCLSpace.ClError, "Cl.ReleaseEvent");
            
#else
            // TODO: cpu code
#endif
        }

        public override void BackPropagate()
        {

        }


        public override void UpdateParameters(double learningRate, double momentumCoefficient)
        {
        }

        #endregion


        #region Obsolete methods

        /// <summary>
        /// Reshape input vector into a matrix of receptive fields, so that convolution can be implemented as matrix multiplication (fast!).
        /// This method is likely to be incredibly SLOW and will soon be ported to OpenCL.
        /// </summary>
        /// <param name="inputVector"></param>
        /// <returns></returns>
        [Obsolete("This method is slow, use the OpenCL kernel instead.")]
        private float[,] UnrollInput(float[] inputVector)
        {
            int nRows = inputDepth * filterSize * filterSize;
            int nCols = outputWidth * outputWidth;
            float[,] unrolledInput = new float[nRows, nCols];

            // Unfortunately there is no way of writing this so that it is readable!
            for (int i = 0; i < nRows; i++)
            {
                int iChannelBeginning = inputWidth * inputHeight * (i / (filterSize * filterSize));

                int iAux1 = (i % filterSize) + inputWidth * ((i % (filterSize * filterSize)) / filterSize);

                for (int j = 0; j < nCols; j++)
                {
                    int iAux2 = (j % outputWidth) + inputWidth * (j / outputWidth);

                    unrolledInput[i, j] = inputVector[iChannelBeginning + iAux1 + iAux2];
                }
            }

            return unrolledInput;
        }

        /// <summary>
        /// Reshape input vector to a matrix so that convolution can be implemented as matrix multiplication (fast!).
        /// This method is likely to be incredibly SLOW and will soon be ported to OpenCL.
        /// </summary>
        /// <param name="outputMatrix"></param>
        /// <returns></returns>
        [Obsolete("Replace this method with an OpenCL kernel!")]
        private float[] OutputMatrixToVector(float[,] outputMatrix)
        {


            int nRows = nFilters;
            int nCols = outputWidth * outputHeight;

            float[] reshapedOutput = new float[nFilters * outputWidth * outputHeight];

            for (int i = 0; i < nRows; i++)
            {
                for (int j = 0; j < nCols; j++)
                {
                    reshapedOutput[i * nCols + j] = outputMatrix[i, j];
                }
            }

            return reshapedOutput;
        }

        /// <summary>
        /// Pad input vector with zeros.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="padding"></param>
        /// <param name="depth"></param>
        /// <param name="height"></param>
        /// <param name="width"></param>
        /// <returns></returns>
        [Obsolete("Replace this method with an OpenCL kernel!")]
        static float[] PadWithZeros(float[] array, int padding, int depth, int height, int width)
        {
            int area = height * width;
            int volume = depth * height * width;
            int zerosPerSlice = 2 * padding * (height + width + 2 * padding);
            float[] paddedArray = new float[array.Length + depth * zerosPerSlice];

            // auxiliary variables
            int iRow, iSlice, iNew;

            for (int k = 0; k < array.Length; k++)
            {
                iRow = (int)((k % area) / width);
                iSlice = (int)((k % volume) / area);

                iNew = k + padding + padding * (2 * padding + width) + 2 * padding * iRow + zerosPerSlice * iSlice;

                paddedArray[iNew] = array[k];
            }

            return paddedArray;
        }


        #endregion
        
    
    }
}
